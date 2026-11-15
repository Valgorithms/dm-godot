# Synthetic map memory baseline

This measures synthetic runtime construction, not Civ13, compiler correctness,
gameplay, or an optimization. It does not call Boot, run a host, tick a world, or
access saves. The generated JSON uses the pinned runtime bytecode version but is
not compiler-produced output.

The fixed matrix has 32², 64² and 128² turf cells, each with 0, 32 or 128 additional
inherited scalar fields. Each map repeats one turf prototype and shares one area.
Each case runs three times in fresh processes, with a tiny discarded warmup.
Reported actual variable counts include the type and parent_type control fields;
the extra fields also exist on the shared area. No entities are created.

Generation, hashing and parsing occur before measurement. A forced full collection
establishes the managed-memory baseline while retaining the parsed project.
Constructor elapsed time, process-wide managed allocated bytes and GC collection
counts are measured separately from the subsequent forced full collection.
The retained delta subtracts the baseline from managed memory after that collection,
keeping both runtime and project alive. It can be negative and is never clamped.
This is not an allocation-site profile or a per-object retained-size measurement.

Working set is sampled after collection and validation; peak working set covers the
entire process, including warmup, parsing and measurement. Neither is peak managed
heap size. Small measurement overhead and runtime/JIT effects remain; the warmup
does not establish steady-state performance. Results include exact engine revision,
compiler compatibility revision, environment including GC mode and processor count, case identity and artifact SHA-256.

Each child has a 2 GiB GC heap cap and 60-second supervisor timeout. The standard
GitHub runner job is bounded to 15 minutes. Failures stop further samples and retain
raw evidence. Aggregation requires all 27 distinct successful samples with matching
provenance and repeat hashes; there are no timing or memory regression thresholds.

After building the pinned dependency and this project in Release, run:
python scripts/benchmark_map_memory.py
Results are written under build/map-memory-benchmark. A nonempty output directory
is refused; archive prior evidence before a manual rerun or use a fresh checkout.
This prevents partial reruns from mixing with old samples. The separate workflow also
runs small real-process checks before the matrix. No runtime or compatibility
probe behavior is changed.

A second paired interval exposes every static object's public Variables dictionary,
including world and unique areas, after the constructor retained sample. Traversal
and deduplication occur before a separate forced-GC exposure baseline; the same
worklist, runtime and project stay alive through the post-exposure sample. Exposure
time, process allocation and retained delta are reported separately. Only then do
tests read dictionary values/order and verify stable public dictionary identity.
This provides a negative control for deferred storage: requesting all dictionaries
must preserve the same values/order, and may pay any deferred allocation cost.
The matrix still contains no movable entities; logical Civ13 type/field counts
in reproduction.json are not a byte-level memory profile or OOM attribution.
