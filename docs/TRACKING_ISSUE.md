# Build a DM-compatible engine and Godot client for unchanged Civ13 and SS13

## Goal

Compile and play existing Civilization 13 and Space Station 13 codebases without
manual edits to their maintained DM source, maps, icons, or interfaces. A
transpiler is acceptable. Preserve observable behavior required by those games;
improve implementation internals and introduce opt-in engine extensions.

This issue tracks the accepted implementation plan from 2026-11-14. The durable
design is in `docs/PLAN.md` and `docs/ARCHITECTURE.md`.

## Architecture decisions

- Reuse a pinned OpenDream compiler frontend. Initially consume its real JSON
  bytecode; evaluate generated C# once runtime semantics are established.
- Keep the DM runtime independent of Godot and networking.
- Use an ordered cooperative scheduler with explicit execution frames.
- Use Godot for presentation and input, with server-authoritative world state.
- Add browser, native-library and persistence adapters as explicit subsystems.
- Report source compilation, runtime execution and gameplay acceptance separately.

## Milestones

- [ ] **M0: executable foundation.** Real DM/DME/DMM compilation; small runtime
  subset; world.New and cooperative scheduling; imported diagnostic map; two
  connected Godot clients; regression tests; explicit unsupported-feature errors.
- [ ] **M1: DM conformance.** Complete values/lists, dynamic dispatch, type and
  object lifecycle, src/usr, waitfor and call suspension, built-ins, and
  differential testing against pinned BYOND.
- [ ] **M2: world and rendering.** Atom containment, full movement callbacks,
  visibility, DMI assets, appearances, overlays, planes, filters, HUD and input.
- [ ] **M3: unchanged Civ13 gameplay.** Full pinned-source compilation, real map
  startup, multiple players, chat, movement, inventory, combat, crafting, browser
  interfaces and native-service integration.
- [ ] **M4: persistence and operation.** Fresh-world save/restart/restore; separate
  migration tests on copies of Gaia saves; recovery, tick-time and memory tests.
- [ ] **M5: second codebase.** Repeat gameplay acceptance for the pinned /tg
  target and expand the supported game/version matrix.

## Initial targets

- Civ13: `98f34be22b7c594de1b9b113f9962cd61f6f0091`, BYOND profile `516.1688`.
- /tg/station: `9b10c26dacec3dc571f07c1e98f8ec903fa51597`.
- OpenDream compiler: `846775a06b8fa4639772cb3e759adcc0133d9711`.

See `compatibility/targets.json` and `docs/compatibility-baseline.md` for source
provenance and the exact current evidence. A sparse Civ13 compiler attempt is
blocked by absent assets and does not establish full compiler compatibility.

## Acceptance boundaries

- New build tooling and a new client are allowed; generated output is disposable.
- Existing DMB binary execution and Dream Seeker wire compatibility are separate
  capabilities, not prerequisites for the source-compatible platform.
- Native-library ABI compatibility and importing historical saves require their
  own tests; source compatibility alone does not establish either.
- Diagnostic player markers do not establish DM mob lifecycle or Move callbacks.
- No production server changes or live-save operations are part of this issue.

## Next implementation slice

After the executable foundation is verified, implement real mob lifecycle and
DM movement hooks, add DMI rendering, and convert Civ13 initialization gaps into
focused conformance fixtures. Keep this issue open until all acceptance gates are
met; link subsequent PRs and evidence here.
