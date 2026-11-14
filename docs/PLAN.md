# Implementation plan

Status: accepted for implementation on 2026-11-14. Working project name: DM-Godot.

## Compatibility contract

Developers continue editing their existing DM, DME, DMM, DMI and interface files.
Build-tool configuration may change; manual game-source conversion is not the
goal. The engine may use a new bytecode, a transpiler, new internal data structures
and a new network protocol. Existing compiled DMB files and the Dream Seeker wire
protocol are separate potential projects, not prerequisites for source support.

Preserve observable language and engine behavior used by supported games. Add new
capabilities through opt-in APIs. Record intentional differences rather than
silently changing semantics. Binary native libraries and historical saves each
need explicit compatibility or migration evidence.

## Milestones and acceptance gates

1. **Executable foundation (current work).** Pin and build OpenDream's compiler;
   compile a valid DM/DME/DMM fixture; load its actual output; execute a documented
   opcode subset and cooperative scheduling; expose a diagnostic grid to two
   Godot clients. Tests must reject unsupported operations explicitly.
2. **DM conformance.** Values, lists, type/proc dispatch, construction/deletion,
   `src`/`usr`, call suspension, `set waitfor`, exceptions, reflection and native
   built-ins. Run differential fixtures against a pinned BYOND toolchain.
3. **World and appearance compatibility.** Full atom containment/movement hooks,
   map creation, visibility, DMI import, appearance inheritance, overlays, planes,
   filters, animation and input routing. Screenshot and gameplay acceptance.
4. **Civ13 playable target.** Compile the pinned target with no manual source
   edits; boot its full world; test login, chat, movement, inventory, crafting,
   combat, browser interfaces and native services with multiple clients.
5. **Persistence and operation.** Save/restart/restore a fresh world. Separately
   import copies of existing Gaia saves. Test recovery and representative memory,
   tick-time and long-running player workloads. No live-world experiments.
6. **Second codebase and expansion.** Repeat against a pinned /tg/station target,
   then declare support per game/revision/engine profile and expand coverage.

The first milestone is infrastructure, not a claim that Civ13 or SS13 is playable.
Passing a tiny fixture cannot substitute for the later acceptance gates.

## Decisions

- Reuse the real OpenDream compiler first; avoid creating another DM parser.
- Begin with an explicitly limited bytecode runtime. Evaluate C# emission after
  the value model, call frames and scheduling contracts are tested.
- Use Godot for presentation and optional hosting; keep the simulation library
  independent so that tests and servers can run without a renderer.
- Preserve a single ordered logical DM scheduler. Parallelize only engine work
  whose results can be committed without changing observable execution order.
- Use a small, versioned diagnostic transport initially. It is not the final
  replication protocol, browser bridge, authentication system, or movement API.
- Report compiler, runtime and graphical acceptance separately.

## Immediate next work after the foundation

Replace diagnostic movement with runtime mob lifecycle and DM movement callbacks;
add standard DMI rendering and appearance snapshots; turn pinned Civ13 compiler
diagnostics and observed runtime gaps into small conformance fixtures. Measure
before selecting a native/JIT/AOT optimization strategy.
