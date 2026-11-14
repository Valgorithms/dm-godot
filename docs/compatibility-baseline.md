# Compatibility baseline

Recorded on 2026-11-14. This baseline fixes the first two acceptance targets;
it does not establish that either game runs on dm-godot. Neither target has
been played, loaded, or saved using this runtime.

## Immutable targets

| Target | Source revision | Entry point | Status |
| --- | --- | --- | --- |
| [Civ13](https://github.com/Civ13/Civ13/tree/98f34be22b7c594de1b9b113f9962cd61f6f0091) | `98f34be22b7c594de1b9b113f9962cd61f6f0091` | `civ13.dme` | Complete unchanged checkout compiled successfully; 137 warnings |
| [/tg/station](https://github.com/tgstation/tgstation/tree/9b10c26dacec3dc571f07c1e98f8ec903fa51597) | `9b10c26dacec3dc571f07c1e98f8ec903fa51597` | `tgstation.dme` | Revision and entry point resolved; no build attempted |

The machine-readable selection is [targets.json](../compatibility/targets.json).
Civ13's selected revision pins BYOND `516.1688` in `tools/byond/version.json`.
The repository's historical OpenDream Windows script still names `516.1681`;
this baseline uses the canonical current pin instead. The source revision was
explicitly checked out after clone because upstream advanced between resolution
and cloning. No target source edits were made.

## Civ13 inventory

The initial source-only inventory included root files, `code/`, `interface/`,
`scripts/opendream/`, and `tools/byond/`. `audit_source.py` inspected 1,786 DM/DME
files comprising 535,667 lines. It found 1,768 explicit quoted include
directives; three referenced maps were excluded from that initial checkout.
All files decoded as UTF-8. The checkout was subsequently made complete for
the successful compiler run below; these inventory counts retain their original
scope and exclude other test/tooling directories.

| Dependency family | Lexical occurrences | Why it matters |
| --- | ---: | --- |
| Appearance | 3,179 | Planes, overlays, render sources, and visual composition need engine implementations. |
| World/runtime | 2,711 | Location, visibility, and movement queries need compatible world data. |
| Scheduling | 1,903 | Sleeping, spawning, and tick budgets require an explicit DM scheduler. |
| Dynamic dispatch | 672 | Dynamic calls, paths, and type inspection must retain DM behavior. |
| Browser interfaces | 137 | Existing UI requires browser/resource/window bridges. |
| Persistence | 88 | Savefile semantics and import/export behavior need acceptance tests. |
| Native/process integration | 24 | Native calls and process APIs need explicit adapters. |

These counts are an inventory of syntax, **not a compatibility percentage**.
The scanner excludes comments, ordinary strings, resource literals, and DM
multiline strings. It does not expand macros, evaluate preprocessor conditions,
resolve calls, or inspect expressions interpolated into strings. It includes
inactive files/branches and may count definitions or user-defined names. The
inventory is therefore useful for prioritization and can both undercount and
overcount runtime dependencies.

Reproduce an inventory from a checked-out target:

```sh
python3 scripts/audit_source.py /path/to/Civ13 --output civ13-inventory.json
python3 -m unittest discover -s tests -p 'test_audit_source.py' -v
```

Six tests cover comment/string exclusion, nested comments, preserved source
locations, Windows-style include paths, includes inside multiline strings,
savefile/native/render/scheduling detection, and bounded sample output.

## Complete compiler result

The full repository tree contains 356,336,958 bytes of blob content (about
340 MiB). All assets were materialized at the same revision before compilation.
The OpenDream compiler at `846775a06b8fa4639772cb3e759adcc0133d9711` was built
and invoked through this project's source-preserving wrapper:

```sh
python3 scripts/compile.py /path/to/Civ13/civ13.dme \
  --output /path/to/output/full-civ13.json --version 516.1688 \
  --dotnet /path/to/dotnet
```

It exited **0** in **11.9 seconds**, reporting **zero errors and 137 warnings**.
The 29,481,555-byte JSON artifact contains 15,633 types, 28,440 procedures,
60,883 strings, and one map. `git status --short` remained empty after the run.
No maintained source or assets were changed, no missing resources were replaced
with placeholders, and no diagnostics were suppressed.

The compiler emitted 126 `OD2800` warnings identifying these upstream engine
gaps, ten `OD3204` operator-precedence warnings, and one `OD0000` warning that
optimizations were disabled. The wrapper intentionally uses `--no-opts` for the
initial runtime; this compilation timing is not a gameplay performance claim.

| Upstream unimplemented feature | Warning occurrences |
| --- | ---: |
| `set src = view()` substitutes range selection | 88 |
| `set instant` | 12 |
| `shell()` | 9 |
| `world.Profile()` | 7 |
| `savefile.ImportText()` | 3 |
| `world.OpenPort()` | 2 |
| `run()` | 2 |
| `obounds()` | 1 |
| `icon.Crop()` | 1 |
| `client.MeasureText()` | 1 |

These are observations from the pinned upstream compiler. They do **not** imply
that the new dm-godot runtime implements everything that compiler does not warn
about. The new runtime starts with a much smaller supported subset.

Counts, representative file/line references, compiler diagnostics, output hashes,
and artifact metadata are recorded in
[civ13-baseline.json](../compatibility/civ13-baseline.json). The full diagnostic
output is preserved in [civ13.compiler.log](../compatibility/civ13.compiler.log).

An earlier sparse-checkout attempt exited 1 with 14,059 missing-file errors
(14,058 `OD0404` resource errors and one `OD1001` missing Gaia map), plus 136
warnings. That superseded attempt is retained as historical evidence in the JSON
baseline. Materializing the original assets resolved every error.

## Next acceptance work

1. Use compiler output to enumerate the instructions, built-ins, and runtime
   hooks required by world initialization. Implement them with focused tests.
2. Load a real map and connect clients; exercise rendering and browser interfaces
   against the existing DM behavior.
3. Verify native integration and persistence using disposable save copies.
4. Repeat the same acceptance sequence for the pinned /tg/station target.

The original acceptance goal remains unchanged source and assets. Existing
BYOND save migration, native-library binary compatibility, and original BYOND
client/server wire compatibility are distinct capabilities and must not be
inferred from compilation or source compatibility.
