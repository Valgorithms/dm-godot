# Smoke world

This small, ordinary DM project exercises the real OpenDream compiler without
conditional compilation for Godot or OpenDream. It includes a 5 x 5 DMM map,
inherited turf properties, one map-placed mob, and a three-state DMI resource.
`generate_icon.py` produces the original geometric test asset; no SS13 or Civ13
artwork is copied. The fixture follows the project's source license.

Expected startup output, with time in DM deciseconds:

| Time | Output |
| --- | --- |
| 0 | `boot` |
| 0 | `sleep-start` |
| 1 | `spawn` |
| 2 | `sleep-end` |
| 2 | `ready` |

The final `ready` must wait for the sleeping callee. This is an execution
semantics test, not just a syntax test. The map has solid border walls and nine
walkable floor cells. The mob starts at (3, 3, 1).

The current Godot diagnostic view may use colors instead of interpreting DMI
states. Successful compilation of `smoke.dmi` proves resource-path retention,
not DMI rendering compatibility.
