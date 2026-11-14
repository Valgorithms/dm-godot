# DM-Godot development

The maintained game language is DM. Preserve unchanged source and assets as the
long-term compatibility contract. Generated C# or bytecode is build output.

Read `docs/PLAN.md` and `docs/ARCHITECTURE.md` before changing contracts. Runtime
behavior must be backed by a fixture, a pinned upstream source, or a BYOND parity
test. Never silently implement an unsupported opcode or built-in as a no-op.
Compilation alone is not proof of gameplay compatibility.

Keep the runtime independent of Godot. Client rendering and networking must not
introduce a Godot Node per DM datum. Keep logical DM execution cooperative and
ordered; do not parallelize game procedures without an explicit semantic design.

Pin upstream revisions. Do not modify `vendor/OpenDream` to make fixtures pass;
check in an explicit patch and document any necessary upstream change instead.
Keep licenses and provenance for any reused source. Run the applicable runtime,
compiler and Godot headless tests. Include limitations and unrun checks in reports.

Do not run tests against live game servers or live save directories. Use isolated
fixtures and copies. Work on assigned paths when multiple agents are active.
