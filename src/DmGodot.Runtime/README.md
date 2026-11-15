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
Original type definitions remain unmodified; native `initial()` fields remain unsupported. Unsupported override diagnostics include type, field and value
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
Compound list removal (-=) remains unsupported. Semantics follow pinned
[NativeRoot.typesof](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/Native/DreamProcNativeRoot.cs)
and ObjectTree descendant traversal; this is not complete reflection support.

Ordinary list subtraction now follows pinned
[DreamList.CreateCopy/OperatorSubtract/Remove](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Objects/Types/DreamList.cs).
It returns a fresh shallow copy, retains only associations whose keys occur in
the positional sequence, and removes the last matching occurrence once per ordinary
RHS list entry (or once for a scalar RHS). Removing a key drops its association even
if duplicate positional keys remain. RHS associations are not removal values.
Inputs remain untouched; nested values preserve reference identity. A strict list
on the RHS is one scalar identity, while strict-list subtraction on the LHS and
custom object subtraction remain unsupported. Numeric/null arithmetic is unchanged.
Copies charge one list plus positional slots and each unique live association pair
before growth. Copy work, comparisons and RemoveAt suffix shifts consume the
instruction budget. Failure does not publish a partial result, and both budgets
retain their existing public execution boundaries. This does not implement -=.

Unfiltered ordinary-list loops support CreateListEnumerator, Enumerate,
EnumerateNoAssign and DestroyEnumerator with the pinned 16 frame-local slots.
Iteration snapshots positional values/keys in order, preserving duplicates, nulls
and shallow references. Later source-list mutation does not replace snapshot entries.
Primitive/null/type/resource and ordinary datum containers create empty snapshots;
atom/world contents and strict associative list containers remain explicit errors.
Filtered, associative, type and range enumeration are still unsupported; association
values are not copied for this values-only subset.

An exhausted present snapshot assigns null through the normal guarded reference
path before jumping. An absent/destroyed slot jumps without assigning; destroying
an empty slot is a no-op, matching the pin. Calls own separate slots; sleep and
waitfor detachment retain the same frame cursor. Spawn starts with empty slots
while preserving its existing copied locals/arguments. Return releases the frame;
Destroy clears its slot. No extra jump-based cleanup is invented.
Snapshots charge one object plus positional entries against the cumulative
allocation budget before copying, and copy work consumes the instruction budget.
Each enumeration opcode consumes its normal instruction allowance. Slot IDs outside
0..15 and malformed jump targets are rejected.
These contracts follow pinned
[DMOpcodeHandlers](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/DMOpcodeHandlers.cs)
GetContentsEnumerator/CreateListEnumerator/Enumerate/DestroyEnumerator,
DreamEnumerators' array cursor, and DMProc's spawn-state copy constructor.

Initial (0x47) supports string-key queries on type references and object identities
for declared immutable instance defaults. It walks original serialized type ancestry,
preserving inherited values and explicit nulls, without consulting current public
Variables, cloned map overrides, the instance-default cache, or any initializer.
A compiler null placeholder remains null even if an initializer would later assign
a list/object/effectful result. Missing fields and null owners return null.

Optional GlobalVariables metadata is retained and validated to identify unsupported
static/global queries. Complete instance Variables precedence is preserved over
static mappings; root global mappings are not inherited. Numeric-index forms,
list/primitive owners, native/metadata fields (including atom name/text) and requested
mutable/procedure encoded defaults remain explicit errors. Other unrelated defaults
and initializer bytecode are not evaluated. Ancestry visits consume the instruction
budget before traversal work; malformed type identities are rejected.
This subset follows pinned
[DMOpcodeHandlers.Initial](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/DMOpcodeHandlers.cs)
and TreeEntry.TryGetTypeVar. It does not implement all forms of initial(), including
native loader-normalized defaults or current static-variable access.


### Explicit file-existence snapshot

The root native `fexists` stub supports exact, case-sensitive relative strings and
resource paths through an optional sealed `DmFileInventory`. No runtime code
queries the host filesystem, current directory, resource cache or file contents.
The inventory defensively copies regular-file names and blocked symlink/submodule
prefixes. A configured empty snapshot returns false; an unconfigured capability
raises an explicit compatibility error. Non-path DM values return null as in
pinned OpenDream `DreamProcNativeRoot.NativeProc_fexists`; empty strings and
resources without paths return false without requiring access.

Paths reject rooted/drive/UNC forms, backslashes, control characters, colon,
wildcards, empty interior components, dot/dotdot and .git components. Safe
directory names and trailing slashes return false; blocked paths and descendants
raise an unsupported-path error. There is no normalization or resource aliasing:
artifact-relative resources containing ../ remain explicitly unsupported.
Inventory ingestion is capped at 100,000 entries, 16 MiB UTF-8 names and 4,096
UTF-16 code units per path. Blocked-prefix ingestion also has a 33,554,432-character
aggregate hashing work limit. Native queries debit bounded work before validation/lookup.

The optional probe manifest is a tracked-source filesystem snapshot built from
the verified pinned Git tree's regular blobs, bound to source commit/tree and
artifact SHA-256. It intentionally excludes untracked/live/persistent state;
symlink and gitlink entries are retained as blocked paths, never followed.
This is a narrower capability than pinned host File.Exists semantics. It does
not itself grant text reads, writes, savefiles or general filesystem access.
Text reads require the separate capability described below.


### Allowlisted source text

An additional explicit `DmTextFileSnapshot` capability implements only the root
empty native `file2text` stub. It must share the exact configured file inventory.
The runtime never opens paths or reads the host filesystem. The snapshot decodes
owned immutable strings once, preserving pinned OpenDream
`DreamResource.ReadAsString`: UTF-8 with replacement fallback, CRLF converted to
LF, BOM/lone CR/embedded NUL retained. Empty file bytes return an empty string;
missing files/directories return null. Non-path arguments and empty paths return
null without access. Unconfigured capabilities, existing but excluded files,
unsafe paths and generated resources without represented bytes fail explicitly.
No mutable byte arrays are retained; subsequent host/input changes cannot alter
the snapshot. This is not live ResourceManager cache/refresh or DMF parsing support.

The opted-in Civ13 probe exposes only the 146 exact public names configuration
paths in `compatibility/civ13-text-files.json`, extracted from pinned
`code/names.dm`. That trusted list also records Git blob IDs and byte lengths.
The builder reads verified regular blobs by immutable object ID with Git replace
objects disabled, never by worktree path. Contents retain the source project's
licensing. The manifest binds source commit/tree, compiled artifact, independently
supplied allowlist hash, raw byte lengths, Git blob IDs and SHA-256 hashes.

Limits are 256 files, 65,536 raw bytes per file, 1,048,576 aggregate raw bytes and
1,048,576 aggregate decoded UTF-16 bytes. The manifest remains capped at 32 MiB,
with base64 length checked before decoding. Every native read charges path work
and returned UTF-16 code units before publishing a result, even when reusing a
cached immutable string. Failed reads do not replace a caller's prior result.
All file writes and other filesystem natives remain unsupported.
