# DM runtime bootstrap

This project executes a deliberately small subset of real OpenDream bytecode.
It is independent of Godot and has no NuGet dependencies. The opcode wire-format
sources are linked from the pinned MIT-licensed OpenDream checkout; the compiler
revision and opcode hash are checked separately by the build tooling and loader.

## Current behavior

- Caches completed inherited constant defaults privately per runtime and type. Only
  immutable converted values enter the cache; each instance still receives its own
  ordinary public Dictionary, preallocated for its final field count. Executable
  initializers and their mutable lists still run per instance. Cache hits retain
  inherited initializer guards, while cold misses preserve ancestor guard/conversion
  order. This is not a sparse overlay or shared mutable instance state.
- Loads the pinned compiler's JSON, inherited constant type variables, globals,
  static DMM turf grids, and mapped entity coordinates.
- Retains each static map turf and mapped movable as a DM object; unoverridden area
  prototypes of the same type share an area object. `MapCells` exposes these identities.
  DM reads of `loc` and coordinates use retained metadata, and instance methods can
  retain ordinary user-variable state. Diagnostic records remain separate snapshots.
- Applies per-cell turf overrides after inherited constant defaults, preserving separate
  instance values and immutable project definitions. The supported order is inherited
  constants, map overrides, then New; executable turf initializers remain rejected,
  including inherited ones. This does not implement general override/init/retype phases. Turf `name` accepts supported raw
  compiler values; DM `.name` reads preserve strings (including empty/formatting text)
  and normalize nonstrings to empty, matching the pinned native setter. Raw values stay
  intact. Name writes and object stringification remain unsupported. Unknown override
  fields fail explicitly (upstream ignores them); metadata/tag/native fields other than
  the existing density/color diagnostic inputs, name and layer remain rejected.
- Static turf `layer` overrides and reads preserve float32 values, including
  fractions, negative values, FLOAT_LAYER (-1), and tagged positive/negative infinities,
  with no clamp or remapping.
  Supported nonnumeric compiler values normalize to zero while raw values remain intact.
  Reads use the final effective value after per-cell overrides. Turf layer writes,
  movable/area layer reads,
  rendering order, plane interactions and appearance propagation remain unsupported.
- Applies scalar DMM overrides to each mapped movable's declared instance variables
  before its pure type initializer and New; initializer assignments can replace
  overrides. Per-instance dictionaries and fresh initializer lists remain isolated,
  and shared type defaults are untouched. Optional compiler ConstVariables metadata
  is retained to reject inherited const overrides. Global/non-instance variables,
  unknown fields, tag/type/parent_type/vars and native fields remain rejected except
  name/icon_state. Rejecting unknown fields is stricter than upstream's ignore behavior.
- Static mapped movable name/icon_state overrides and reads preserve their raw values.
  Native name reads keep strings and normalize other overridden values to ""; native
  icon_state reads keep strings (including empty) and normalize nonstrings to null.
  Null type-default names without map overrides use the final path component with
  underscores replaced by spaces, matching upstream's type-name fallback. No icon
  loading/state validation is implied. Only mapped movables gain icon_state reads;
  existing turf behavior stays unchanged. Native writes, rendering/appearance
  propagation and object stringification remain unsupported.
- Implements coordinate `locate(x, y, z)` using the retained turf index. Repeated
  lookups return the same object. Finite numbers truncate toward zero before bounds
  checking; non-numbers coerce to zero, and invalid/out-of-bounds coordinates return
  null. Non-finite numbers and truncated values outside the signed 32-bit integer
  range raise source-located compatibility errors; host overflow casts are unsupported.
  Type/tag/container `locate()` and native contents views remain unsupported.
- Initializes map-area user variables from literal constants and fresh positional, associative and dimensioned
  list literals, including nested lists and inherited initializer overrides. The entire
  inherited initializer chain is checked before that area's code executes. Only compiler
  initializer super calls and own user-field assignments are accepted: helpers, reads,
  output, globals, native/metadata assignments, object construction, sleep and spawn
  remain errors. Constructor-time initialization has a fixed aggregate 100,000-instruction
  budget (the configurable InstructionBudget applies after construction). This narrow
  subset has no observable preboot effects; it does not implement general startup phases.
- Preflights every mapped movable's complete initializer chain during loading, using
  the same literal-only validator as areas. Each movable's initializer then runs once
  at its own Boot position, after all turf New prefixes and immediately before its New.
  Initializers receive no arguments and cannot read src/usr/loc, other fields, globals,
  or call helpers. The map location is retained eagerly; this restricted subset cannot
  observe that difference from upstream's init-then-native-loc ordering.
- Ordinary associative list literals preserve ordered entries, duplicate-key replacement,
  null-key positional entries, list/object key identity and numeric positional writes.
  Numeric list indices truncate toward zero; constructor writes may append exactly at
  length+1, but gaps and zero indices fail. Numeric replacement preserves existing
  associations, matching pinned upstream. This is not the distinct `alist` type.
- Strict associative list literals (`alist`) use a separate reference-identity value.
  Every constructor pair is a dictionary entry, including null keys. Numeric keys are
  exact float32 values, never positional indices: zero, negative values, fractions,
  infinities and NaN are retained. Float equality treats signed zero as equal and
  repeated NaN keys as equal. Duplicate keys replace values without increasing length;
  list, alist and datum keys retain reference identity. Missing keys read as null.
  Index reads/writes, `length()` and `.len` reads work; even empty alists are truthy.
  Constructor allocation is charged before materialization; indexed insertion charges
  two slots only for a new key, and rejects an over-budget insertion before mutation.
  Fresh inherited alist/nested-list initializer values are never cached as defaults.
- Dimensioned lists create distinct intermediate lists and null leaves. Finite numeric
  dimensions truncate; nonnumbers become zero; negative dimensions in multidimensional
  constructors become zero. Negative single dimensions are explicitly unsupported
  (upstream exposes a configurable negative-size exception). Nonfinite/out-of-range
  sizes and constructor numeric keys fail explicitly. Constructors have a 32-dimension
  limit and an aggregate budget of 100,000 list objects/value slots per constructor,
  Boot, InvokeGlobal or AdvanceBy execution boundary; associative pairs conservatively
  consume two slots. The instruction budget remains separate.
- Starts static map `New()` callbacks after nonyielding global initialization: unique
  grid areas in ascending z/y/x order, then an unused default area, turfs in ascending z
  and descending y/x order, then movables in map/block order with bottom-up rows and
  ascending x (cell object order retained), then `world.New()`. Each is an independent
  root executed to its first yield; deferred work drains only after every root starts.
  Movable constructors receive their turf as argument 1 with `loc` already set.
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
Enter/Exit, or collision callbacks. Executable world/turf type
initializers and area/movable initializers outside the local-literal subset, sparse/overlapping maps, unsupported map entity overrides and all map area overrides are rejected
rather than silently ignored. Static identities are eagerly allocated before New;
contents/type enumeration and dynamic atom creation remain unavailable, so this
slice does not claim general construction timing parity. The default area is retained
and receives New even when no cell uses it, provided the map has at least one z level. Static loc links are readable (movable to turf, turf to area, area to null).
Single-z area coordinates use the lowest turf by y, then x; coordinate reads on areas
spanning multiple z levels fail explicitly because pinned upstream cross-z cache behavior
needs separate parity investigation. Their identity and null loc remain available. Native atom/appearance, movable and mob fields beyond loc/coordinates/type metadata
remain explicit errors, except static turf name/layer and static movable name/icon_state reads; this includes contents, color,
bounds and connection/vision fields.
This avoids exposing unnormalized compiler defaults as runtime behavior. Mutation of
those fields remains rejected so retained objects cannot silently diverge from snapshots.
Direct mapped-object output also fails until native name/stringification is implemented.
Ordinary user-declared variables remain readable and writable.
Full containment/movement, native managed object creation,
GC/deletion, strict-list operators/enumeration/native methods, dynamic calls, native interfaces,
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
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/map-new.dme --output build/map-new.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/area-init.dme --output build/area-init.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/area-init.dme --output build/area-init-optimized.json --optimize
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/turf-name.dme --output build/turf-name.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/movable-init.dme --output build/movable-init.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/movable-init.dme --output build/movable-init-optimized.json --optimize
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/alist.dme --output build/alist.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/alist.dme --output build/alist-optimized.json --optimize
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/entity-overrides.dme --output build/entity-overrides.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/entity-overrides.dme --output build/entity-overrides-optimized.json --optimize
dotnet run --project tests/DmGodot.Runtime.Tests -- build/smoke.json build/semantics.json build/semantics-optimized.json build/map-new.json build/area-init.json build/area-init-optimized.json build/turf-name.json build/movable-init.json build/movable-init-optimized.json build/alist.json build/alist-optimized.json build/entity-overrides.json build/entity-overrides-optimized.json
```

Tests exercise code emitted by the real compiler, scheduler interactions, map
coordinates, source diagnostics, malformed artifacts, and bounded execution.

Empty methods owned by ordinary user datum types return null. The pinned compiler
JSON has no native flag, so empty built-in-owner and global procedures remain
explicit compatibility errors (apart from the existing New/Del defaults). This
uses the pinned native registry's absence of custom ordinary-datum native owners.

## Pinned initialization/list evidence

The contract above follows OpenDream revision
`846775a06b8fa4639772cb3e759adcc0133d9711`:
[InitializeAtoms / LoadMapObjectsAndMobs](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Map/DreamMapManager.cs),
[InitDreamObjectState.Resume](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/InitDreamObject.cs),
[DreamObjectMovable.Initialize](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Objects/Types/DreamObjectMovable.cs),
[ordinary list constructor opcodes](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/DMOpcodeHandlers.cs),
and [DreamList.Initialize/Set](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Objects/Types/DreamList.cs).

Mapped movable initialization occurs after StartWorld sets Initialized=true, so
upstream nested object construction would run New. This subset rejects nested new
and does not reuse prestart New suppression. Eagerly retained movable identities
are still a limitation of the static map model, not full allocation-phase parity.
Pinned Civ13's map_metadata declares numeric-key `alist` values and has an
effectful New; list initializer support does not establish Civ13 boot or gameplay
compatibility. Strict-list type/islist/istype introspection, stringification, field
writes (including len/type), operators, enumeration, native methods and direct
new /alist remain explicit errors. Compiler JSON tag 6 remains unsupported rather
than introducing mutable alists into the immutable default cache. Compiler-mode fixtures are not BYOND differential results.

Strict-list behavior additionally follows
[DreamAssocList.Set/GetElement/GetLength](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Objects/Types/DreamAssocList.cs)
and `DMOpcodeHandlers.CreateStrictAssociativeList` at the same revision.
Bare `null = value` in source is parsed as the string key "null"; `(null) = value`
retains a true null key. Both forms are covered by compiler-mode fixtures.

Map entity override order follows pinned `DreamMapManager.CreateMapObjectDefinition`
and `LoadMapObjectsAndMobs`: overrides affect a cloned definition before construction,
type initialization, native location initialization and New. This runtime accepts
only the bounded static subset described above; it does not implement dynamic map
loading, complete native initialization or constant-field override semantics.
Name/icon_state normalization follows
[AtomManager.SetAppearanceVar/GetAppearanceVar](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/AtomManager.cs).
Original type definitions remain unmodified; this slice does not add runtime
`initial()` support. Unsupported override diagnostics include type, field and value
category without printing user values.

Instance variable storage shares immutable, eagerly validated type defaults until
the first instance write or public Variables access. Either event creates the full
independent ordinal dictionary in the existing key order. The public getter returns
the same mutable Dictionary thereafter; host Remove/Clear/Add and DM writes share
that dictionary without restoring removed defaults or metadata. Initializer-created
lists remain per-instance. This defers copies for untouched objects, not validation
or initializer execution, and fully exposing all dictionaries pays the copy cost.

The native root typesof stub supports type references, object identities and exact
type-path strings. It returns a fresh ordinary list including each root, followed
by actual Parent descendants in preorder with ascending serialized child IDs;
arguments concatenate and preserve duplicates. Null, ordinary lists, numbers and
resources are ignored as in the pin. Exact string lookup excludes root ID zero;
unknown strings fail. Proc/verb paths and strict associative list identities remain
explicit capability errors. User-defined procedure bodies are not intercepted.
Type-index construction, string searches, arguments and traversal charge the
instruction budget; result objects/slots charge the existing aggregate list budget
before allocation/growth. No type defaults, constructors or callbacks execute.
List subtraction remains unsupported. Semantics follow pinned
[NativeRoot.typesof](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/Native/DreamProcNativeRoot.cs)
and ObjectTree descendant traversal; this is not complete reflection support.
