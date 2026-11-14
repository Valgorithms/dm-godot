# Foundation validation - 2026-11-14

The complete local verification runner passed all 11 stages in 16.2 seconds.
This is an executable foundation checkpoint, not full-game compatibility.

| Check | Result |
| --- | --- |
| Pinned OpenDream compiler bootstrap | Passed, unmodified upstream source |
| Real DM smoke and scheduler fixture compilation | Passed; deliberate `--no-opts` notice |
| Runtime/host/test Release solution build | Passed; 0 warnings, 0 errors |
| Compiler relocation/resource/failure tests | 2 passed |
| Runtime and malformed-artifact tests | 55 passed |
| Godot editor import and script parse | Passed |
| Godot protocol validation | 14 cases passed |
| Godot socket integration tests | 4 passed |
| Source inventory tests | 6 passed |
| Real host tests | 4 passed |
| Two real Godot clients against the real host | Passed; both saw both IDs and confirmed movement |
| Independent retest of four review defects | 4 passed; controlled errors instead of host crashes/placeholder behavior |

Toolchain: .NET SDK 10.0.404 / runtime 10.0.15; Godot standard
4.7.2.stable.official.ed1daf0bf; Python 3.12. The initial client checks also
passed under the preinstalled Godot 4.6.3.

## Reproduce

```sh
python3 scripts/verify.py
```

Set `DM_GODOT_DOTNET` and `GODOT` to absolute executable paths when needed.
The runner does not silently skip missing tools. It uses real compiler output,
the C# host and actual Godot processes. Network ports are assigned by the OS.

The raw runner output is in
[`compatibility/foundation-verification.txt`](../compatibility/foundation-verification.txt).
The two-client evidence is in
[`compatibility/foundation-end-to-end.json`](../compatibility/foundation-end-to-end.json).

The DM smoke trace is:

| Virtual deciseconds | Output |
| ---: | --- |
| 0 | boot |
| 0 | sleep-start |
| 1 | spawn |
| 2 | sleep-end |
| 2 | ready |

## Full-game compilation

The complete, unmodified Civ13 checkout at
`98f34be22b7c594de1b9b113f9962cd61f6f0091` compiled through the new wrapper:
zero errors, 137 warnings, 11.9 seconds. Output contains 15,633 types, 28,440
procedures and one map. Warning breakdown, hashes, source pin and raw compiler
log are in the [compatibility baseline](compatibility-baseline.md).

## Limits of this evidence

The Godot checks run headlessly; no graphical DMI/appearance acceptance has been
performed. The diagnostic host moves protocol markers, not DM mobs. The new
runtime has not booted or played Civ13 or /tg/station. BYOND differential
conformance, performance comparison, native libraries, browser UI and save
migration remain untested. GitHub Actions has a workflow prepared but has not
run because repository publication is pending.
