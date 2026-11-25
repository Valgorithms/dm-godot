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
- Validates mapped turf initializers once per concrete type. A compiler-generated
  super call initializes ancestor fields before descendant fields. Initializers can
  assign literal values, positional lists and single-entry lists keyed by a literal
  type to declared user fields. Each cell gets fresh lists, including nested lists.
  Positional list creation and packed literal instructions accept up to 256 entries
  apiece. Calls such as `matrix()`, reads, yields and writes to native fields fail
  explicitly. Disjoint DMM overrides are visible to turf `New()`. An override
  targeting an initializer assignment is rejected because the evaluation order
  for a shadowed assignment has not been established.
  Validation, copying inherited fields and executing bytecode share the finite
  constructor work allowance. List allocations use the existing slot limit.
- Turf `name` accepts supported raw compiler values. DM `.name` reads preserve strings (including empty/formatting text)
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

### Boot ownership

The compatibility probe constructs a fresh runtime inside a disposable
`DmStartupSession`. It calls `AdvanceGlobals(256)` until the method returns true,
then calls `Advance()` to run mapped callbacks, `world.New()` and the initial
queue drain and take ownership of the completed runtime. Callers using only
`Advance()` can still run global initialization in one call and transfer the
world in the next. Synchronous `Boot()` uses the same interpreter and budgets.

`AdvanceGlobals(n)` accepts a positive dispatch count. A dispatch is one opcode,
construction stage or implicit frame return. Nested calls and immediate-spawn
children spend the same allowance. A pause preserves the current instruction,
call stack, queued work and virtual time. After globals finish, additional calls
to `AdvanceGlobals(n)` return true until the session closes. The cumulative VM
work, list-allocation and split-output budgets span all of these calls.

After each global-initializer step, `session.GlobalWork` reports `StepSpent` and
`Remaining` in VM work units. `StepSpent` includes each admitted dispatch and
native charge, even if a later operation faults. An atomic native operation can
consume more units than the dispatch count. `Remaining` is the cumulative global
allowance left at that point. The probe records the pair as `global_step_work`
and `global_work_remaining`. Both are null before the first step. Completion
polls, later boot phases and disposal preserve the last pair. A project without a
global initializer reports zero spent and the full allowance on its first step.

The session accepts the same immutable file inventory and text snapshot as the
runtime constructor. After an advance, `LogCount` and `PendingThreads` report
scalar counts, including on failure. A fault closes the session. Cancellation
drops a parked world and is checked during map construction, bytecode dispatch,
startup callbacks and before transfer. Disposal waits for an active advance.
Use `using` to release an abandoned session. Cancellation after handoff leaves the returned runtime available to its owner.

A native call runs to completion or fault within its dispatch, including all
of its existing work charges. Map construction and the final boot phase also
run synchronously. These operations can delay a cancellation check. The probe
supervisor enforces a wall-clock timeout on the probe process.

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
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/turf-name.dme --output build/turf-name-optimized.json --optimize
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/movable-init.dme --output build/movable-init.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/movable-init.dme --output build/movable-init-optimized.json --optimize
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/alist.dme --output build/alist.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/alist.dme --output build/alist-optimized.json --optimize
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/entity-overrides.dme --output build/entity-overrides.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/entity-overrides.dme --output build/entity-overrides-optimized.json --optimize
dotnet run --project tests/DmGodot.Runtime.Tests -- build/smoke.json build/semantics.json build/semantics-optimized.json build/map-new.json build/area-init.json build/area-init-optimized.json build/turf-name.json build/movable-init.json build/movable-init-optimized.json build/alist.json build/alist-optimized.json build/entity-overrides.json build/entity-overrides-optimized.json build/turf-name-optimized.json
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
Original type definitions remain unmodified; native `initial()` fields remain
unsupported. Unsupported override diagnostics include type, field and value
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
Every query with a recognized root charges the full type count before using the
child index, including repeat queries. Arguments, string searches and descendant
visits also charge the instruction budget. Result lists and slots charge the
aggregate list allocation budget.

Runtimes sharing a compiled project reuse its completed child index. Each parsed
type holds two integer links. On first use, the runtime fills the links in reverse
ID order, at most 256 type records per locked batch. It exposes the index to
traversal only after all records have been processed. The normal call completes
synchronously. Index construction and traversal execute no DM callbacks.
Compound list removal (-=) remains unsupported. Semantics follow pinned
[NativeRoot.typesof](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/Native/DreamProcNativeRoot.cs)
and ObjectTree descendant traversal; this is not complete reflection support.

`typesof` string lookup charges the input UTF-16 length and ten units for its
fixed ordinal suffix checks, then one unit per candidate's metadata/length check
and the entire candidate length before each equal-length ordinal comparison.
Long shared prefixes therefore cannot bypass the instruction budget. Unequal
lengths require no character scan. Unknown-path errors report only the numeric
UTF-16 length, never the input text. This changes resource limits/diagnostics,
not exact case-sensitive lookup, root exclusion, or proc/verb guards.

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

Subtraction charges UTF-16 text work for strings and resource/type-reference paths
before hashing and equal-length comparisons, including dictionary collisions.
Unknown host value representations in compared/copied positional entries or
association keys are rejected without invoking their Equals/GetHashCode methods.
Nested DM lists/objects retain shallow reference identity. Association values are
copied as values without invoking equality. A metered temporary association index
avoids uncharged lookup in the source dictionary; its separate scratch limit is
100,000 logical object/key/value slots, including orphan keys. Source dictionary
capacity (including tombstones), scratch initialization, output dictionary growth,
comparison scans and suffix shifts charge work before execution. Output allocation
still charges only the result object, positional slots and unique live pairs.
The temporary comparer detaches its runtime callback on success or failure; the
returned public dictionary then has ordinary DmValue equality/hash behavior.

Every scratch/output dictionary operation also debits its populated entry count
before traversing a bucket: different stored hashes can collide in one bucket
without invoking Equals. This conservative bound may stop association-heavy
operations earlier even with well-distributed hashes. Positional-only subtraction
has no dictionary indexing or such charge. The scratch cap is per operation; the
existing output allocation allowance remains cumulative per public boundary.

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
allocation budget before copying. Shallow positional representation validation and
copying each consume one work unit per entry; unsupported host-only values reject
before publishing a snapshot, without invoking their equality or formatting code.
Nested list/object references are retained, not recursively inspected. Object-container
classification consumes one work unit per actual Parent link, with fixed-length
ordinal native-path checks; forged native list/alist object wrappers reject explicitly.
Each enumeration opcode consumes its normal instruction allowance. Slot IDs outside
0..15 and malformed jump targets are rejected.
These contracts follow pinned
[DMOpcodeHandlers](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/DMOpcodeHandlers.cs)
GetContentsEnumerator/CreateListEnumerator/Enumerate/DestroyEnumerator,
DreamEnumerators' array cursor, and DMProc's spawn-state copy constructor.

The supported non-world mutation snapshot and positional-key iteration also follow
the [BYOND DM Reference for list loops](https://www.byond.com/docs/ref/#/proc/for/list).
This is source/documentation evidence, not a native-engine execution result; native
world/atom contents, filtered and key/value loops remain outside this slice.
Sleeping frames intentionally retain their snapshots. These per-public-execution
work/allocation limits do not establish a global retained-heap cap, and this slice
does not add object deletion or BYOND reference-count lifecycle compatibility.

Initial (0x47) supports string-key queries on type references and object identities
for declared immutable instance defaults. It walks original serialized type ancestry,
preserving inherited values and explicit nulls, without consulting current public
Variables, cloned map overrides, the instance-default cache, or any initializer.
A serialized null is supported only when its nearest declaring type has no own
executable initializer. Executable declarations use null placeholders whose BYOND
initial values are not necessarily null. Those ambiguous defaults reject explicitly,
including explicit nulls sharing that declaring type with unrelated initialization.
Initializers on other ancestor/descendant types do not invalidate a proven nearest
declaration. Missing fields and null/numeric/text owners return null.

Optional GlobalVariables metadata is retained and validated to identify unsupported
static/global queries. Complete instance Variables precedence is preserved over
static mappings; root global mappings are not inherited. Numeric-index forms,
list/resource owners, native/metadata fields (including atom name/text) and requested
mutable/procedure encoded defaults remain explicit errors. Other unrelated defaults
and initializer bytecode are not evaluated. Ancestry visits consume the instruction
budget before traversal work; malformed type identities are rejected.
The opcode/metadata subset follows pinned
[DMOpcodeHandlers.Initial](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/DMOpcodeHandlers.cs)
and TreeEntry.TryGetTypeVar. It does not implement all forms of initial(), including
native loader-normalized defaults or current static-variable access.

Initial queries debit input field-name UTF-16 length, each ancestry/metadata entry,
and equal-length ordinal field-name comparisons before the work. This explicit
scan avoids unmetered dictionary collision chains. Selected values additionally
charge their encoded UTF-8 byte length before decoding or tagged-property scans;
unrelated values remain untouched. Diagnostics do not echo unbounded field names.
Repeated calls share the existing public-execution work budget; this is conservative
work accounting, not a general retained-memory cap.

The executable-null guard intentionally differs from OpenDream's raw-placeholder
result: a bounded native BYOND 516.1688 fixture observed non-null dynamic object/list
initial values. Numeric/text owners with string fields returned null in that fixture.
No initializer is executed to manufacture a result. Declaring-type provenance follows
DMCodeTree.Vars.TrySetVariableValue: nonconstant assignments/overrides emit their own
type's null placeholder plus initializer assignment.


### Explicit file-existence snapshot

The empty root native `fexists` stub accepts exact case-sensitive relative string
paths through an optional sealed `DmFileInventory`. Runtime code never queries the
host filesystem, cwd, resource cache or file contents. An unconfigured string query,
including the empty root, raises a capability error. A configured snapshot returns
one for its empty logical root, listed files (including zero-byte files), and implicit
directories with or without a trailing slash. Missing paths and a regular filename
with a trailing slash return zero. Empty directories absent from the tracked names
cannot be represented; this is a tracked-source snapshot, not a live filesystem.

Non-path values return null. Compiler embedded resource values also return null,
matching the measured BYOND 516.1688 ordinary text-resource fixture. Their paths do
not bypass the capability or cause lookups. This intentionally differs from pinned
OpenDream's resource-path coercion. Native `file()` references have different native
behavior and remain unsupported; no resource contents or generic file handles are
represented by this slice. Real compiled literal/file() fixtures protect that boundary.

Paths reject rooted/drive/UNC forms, backslashes, control characters, colons,
wildcards, empty interior components, dot/dotdot and .git components. No aliases,
case folding, path normalization or host-dependent separator handling are performed.
Symlink/submodule paths and descendants reject explicitly. Files and blocked paths
must not duplicate each other or conflict with implied directory ancestry.

The inventory defensively copies names into ordinal-sorted arrays. Binary searches
charge bounded comparison work, including shared prefixes and blocked ancestry;
there are no inventory hash collision chains or query-dependent retained callbacks.
Validation and the sole directory-prefix allocation are charged before work. Input
caps are 100,000 entries, 16 MiB UTF-8 names and 4,096 UTF-16 code units per path.
Construction sorting and hierarchy checks share a 33,554,432-unit comparison/work
limit. These conservative bounds can reject otherwise valid large inventories or
queries rather than silently omit names. Repeated DM calls share the normal public
execution budget; host construction limits are separate.

The optional producer/probe manifest contains only regular Git tree file names
and blocked symlink/gitlink names, bound to source commit/tree and artifact SHA-256.
Git replacement objects are disabled and metadata reads are bounded. It excludes
untracked/live/persistent state; no symlink is followed and no file content capability
is granted by the names capability. It does not grant file(), writes, savefiles or
unrestricted filesystem access; text reads require the separate capability below. Primary evidence: pinned
[NativeProc_fexists](https://github.com/OpenDreamProject/OpenDream/blob/846775a06b8fa4639772cb3e759adcc0133d9711/OpenDreamRuntime/Procs/Native/DreamProcNativeRoot.cs)
and bounded native BYOND 516.1688 text/root/directory/resource fixtures; the deliberate
resource/directory differences above are not claimed as OpenDream parity.

### Allowlisted immutable source text

The root empty native `file2text` stub supports a separate explicit
`DmTextFileSnapshot`, bound by reference identity to the same configured file
inventory. Runtime code performs no host I/O. Non-path DM values return null;
string paths require the text capability, even for the logical
root. Missing snapshot paths return null. Known directories/root and zero-byte
allowed files return empty text. A tracked regular file without allowed content
raises an explicit capability error rather than pretending the file is missing.
Unsafe paths and native file() handles remain unsupported. Embedded resource values
reject explicitly: the artifact only carries a path, not independently bound packed
bytes. Native fixtures demonstrate that embedded literals can retain compile-time
bytes when loose string/file() paths change. This capability must not substitute a
loose file for a packed resource, even if names match. No resource aliases, cache
refresh, or packed-resource content store is implemented.

Decoding follows bounded BYOND 516.1688 byte-fixture evidence, not the incompatible
OpenDream replacement-decoder behavior: truncate at the first NUL byte, remove a
leading UTF-8 BOM, decode strict valid UTF-8, and remove all carriage returns.
If strict UTF-8 decoding fails without a leading UTF-8 BOM, the supported Latin-1
subset requires each high byte to follow an ASCII letter and precede either another
ASCII letter or LF. Before LF, the supported range is 0x80 through 0xFE. BYOND
preserved the raw 0xFF byte at that boundary. The decoder checks the original
bytes before removing carriage returns. Other malformed sequences raise a compatibility error, including adjacent
high bytes, high bytes at EOF and high bytes immediately before CRLF. BYOND
516.1688 fixtures cover all 128 high-byte values in both positions and the
complete contents of both approved name files.

The target probe grants `config/names/first_male.txt` and `config/names/first_female.txt`
from the pinned public Civ13 source. Each regular Git blob ID, byte length and SHA-256
must match `compatibility/civ13-text-allowlist.json`.
Producer reads immutable Git object IDs with replacement objects disabled; it never
uses worktree file contents. The content manifest is bound to source commit/tree,
compiled artifact and separately supplied allowlist hash. Source licensing remains
applicable. Names-only inventory configuration grants no text access by itself.

Limits: 256 files, 65,536 raw bytes per file, 1,048,576 aggregate raw bytes and
1,048,576 aggregate normalized decoded UTF-16 bytes. Per-file intermediate decoding
is bounded by its raw cap; stored strings are immutable. Query work uses the metered
inventory and ordinal binary text lookup, with no hash-collision-dependent scans.
Every returned UTF-16 code unit is charged before publication, even for repeated
reads reusing the same string. Construction has bounded metadata/work limits and
no callbacks are retained. Failure preserves the caller's prior result. Manifest
and base64 ingestion are separately bounded by the producer/probe. This does not
add file writes, file(), general resource loading, DMF parsing or live filesystem
refresh/persistence semantics.

### Bounded full-range splittext

The root empty native `splittext` stub supports full-range literal string delimiters
with exact case-sensitive, non-overlapping matching. Multi-character delimiters and
valid Unicode strings are supported; leading, adjacent and trailing separators
preserve empty fields. Include-delimiters accepts exactly zero/one (or null/missing
as zero); one inserts each matched delimiter between fields. Empty string delimiters
split ASCII input into one-character entries; include-one inserts empty strings
between characters. Non-ASCII empty-delimiter input rejects because native byte
splitting cannot be represented faithfully as ordinary Unicode character splitting.

The compiler-supported arity is two to five arguments; malformed arity rejects first.
Empty/nontext source then returns a fresh empty list before inspecting other arguments.
Only missing/null/default Start=1 and End=0 are supported. Nondefault indexed search
bounds, nonstring/null delimiters, other include flags, malformed UTF-16 host strings,
regex and extra-argument forms reject explicitly. In particular, unsupported null/list
delimiters are not silently replaced by an empty result: native fixtures show their
byte-splitting behavior. Executable user-defined proc bodies are not intercepted.

Native BYOND 516.1688 fixtures establish the supported output sequence, overlap,
case, Unicode and ASCII-empty cases; pinned OpenDream's empty-text and delimiter
coercion differences are not copied. No indexed-byte or general regex parity is
claimed. The target dependency is ordinary newline splitting of an explicitly
allowlisted names string; no additional file content is granted by this operation.

A configured text snapshot can certify the source string's UTF-16 validity by
reference identity. Strict UTF-8 decoding and the supported Latin-1 fallback
produce well-formed UTF-16, and CR removal preserves surrogate pairs. For a
source longer than the snapshot's file count, the runtime charges one unit per
file before checking the stored references (at most 256). An exact match saves
the per-code-unit validation scan. A miss pays this bounded lookup cost and
then the full validation cost. Short sources use the original scan directly.
Delimiters always receive their ordinary validation.

Each UTF-16 validation scan, literal comparison and emitted code unit consumes the
work budget before work/allocation. Adversarial repeated-prefix scans can exhaust
that budget explicitly. The implementation does not use an unbounded String.Split
intermediate. Outputs charge the existing aggregate 100,000 list-object/slot budget
and a separate 1,048,576-byte UTF-16 output budget before adding/substr copying,
including reused delimiter strings. Both allocation budgets reset only at public
Boot/AdvanceBy (and therefore InvokeGlobal) boundaries; Boot's internal deferred
queue drain does not reset them. No partial output is published on failure. These
limits bound each public execution's output, not all retained strings across calls.
