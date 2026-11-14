using DmGodot.Runtime;
using DMCompiler.Bytecode;
using System.Text.Json.Nodes;

var path = args.Length > 0 ? args[0] : "build/smoke.json";
var tests = new List<(string Name, Action Run)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action, string contains) {
    try { action(); throw new Exception("Expected CompatibilityException."); }
    catch (CompatibilityException ex) { Check(ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase), $"Expected '{contains}', got {ex.Message}"); }
}

tests.Add(("real compiler artifact / map dimensions and density", () => {
    var runtime = new DmRuntime(CompiledProject.Load(path));
    Check(runtime.Width == 5 && runtime.Height == 5 && runtime.Levels == 1, "Dimensions changed.");
    Check(runtime.Tiles.Count == 25, "Expected 25 turfs.");
    Check(runtime.Tiles.Count(t => t.Density) == 16, "Expected 16 walls.");
    Check(runtime.MapEntities.Single().Type == "/mob/player", "Missing mapped mob.");
    Check(runtime.MapEntities.Single().X == 3 && runtime.MapEntities.Single().Y == 3, "Mapped mob coordinate changed.");
}));
tests.Add(("nested sleep suspends caller; spawn runs independently", () => {
    var runtime = new DmRuntime(CompiledProject.Load(path));
    runtime.Boot();
    Check(runtime.Logs.Select(x => x.Text).SequenceEqual(new[] { "boot", "sleep-start" }), "Boot execution order.");
    runtime.AdvanceBy(1);
    Check(runtime.Logs.Select(x => x.Text).SequenceEqual(new[] { "boot", "sleep-start", "spawn" }), "spawn did not execute while parent slept.");
    runtime.AdvanceBy(1);
    Check(runtime.Logs.Select(x => x.Text).SequenceEqual(new[] { "boot", "sleep-start", "spawn", "sleep-end", "ready" }), "sleep did not restore callers.");
    Check(runtime.Logs.Select(x => x.Time).SequenceEqual(new double[] { 0, 0, 1, 2, 2 }), "Decisecond scheduling mismatch.");
    Check(runtime.PendingThreads == 0, "Leaked scheduled thread.");
}));
tests.Add(("opcode version mismatch rejected", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Metadata"]!["Version"] = "wrong-version";
    Throws(() => CompiledProject.Parse(json.ToJsonString()), "bytecode version");
}));
tests.Add(("truncated bytecode rejected before execution", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Procs"]![1]!["Bytecode"] = Convert.ToBase64String(new byte[] { 0x03, 0x01 });
    Throws(() => CompiledProject.Parse(json.ToJsonString()), "truncated");
}));
tests.Add(("unknown opcode rejected before execution", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Procs"]![1]!["Bytecode"] = Convert.ToBase64String(new byte[] { 0xff });
    Throws(() => CompiledProject.Parse(json.ToJsonString()), "unknown opcode");
}));
tests.Add(("unsupported operation reports DM source", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Procs"]![1]!["Bytecode"] = Convert.ToBase64String(new byte[] { 0x3d }); // browse, valid opcode but deliberately unsupported
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    Throws(runtime.Boot, "world.dm:8");
}));
tests.Add(("bad jump target rejected", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Procs"]![1]!["Bytecode"] = Convert.ToBase64String(new byte[] { 0x0e, 2, 0, 0, 0 });
    Throws(() => CompiledProject.Parse(json.ToJsonString()), "instruction boundary");
}));
tests.Add(("non-yielding loop has bounded execution", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Procs"]![1]!["Bytecode"] = Convert.ToBase64String(new byte[] { 0x0e, 0, 0, 0, 0 });
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())) { InstructionBudget = 100 };
    Throws(runtime.Boot, "budget exhausted");
}));
tests.Add(("type parent cycle rejected", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Types"]![1]!["Parent"] = 1;
    Throws(() => CompiledProject.Parse(json.ToJsonString()), "cyclic");
}));

tests.Add(("null metadata is rejected cleanly", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Metadata"] = null;
    Throws(() => CompiledProject.Parse(json.ToJsonString()), "required metadata");
}));
tests.Add(("custom map constructors are rejected rather than skipped", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var mappedType = json["Types"]!.AsArray().First(t => t!["Path"]!.GetValue<string>() == "/mob/player")!;
    mappedType["Procs"] = new JsonArray(new JsonArray(JsonValue.Create(1)));
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Map New() lifecycle");
}));
tests.Add(("direct unknown native procedure invocation fails explicitly", () => {
    var runtime = new DmRuntime(CompiledProject.Load(path)); runtime.Boot();
    Throws(() => runtime.InvokeGlobal("alert"), "native proc");
}));

void Malformed(string name, Action<JsonNode> mutate) {
    tests.Add((name, () => {
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        mutate(json);
        Throws(() => CompiledProject.Parse(json.ToJsonString()), "artifact");
    }));
}
Malformed("null Types array rejected", j => j["Types"] = null);
Malformed("null Procs array rejected", j => j["Procs"] = null);
Malformed("null Strings array rejected", j => j["Strings"] = null);
Malformed("null type entry rejected", j => j["Types"]![0] = null);
Malformed("null type path rejected", j => j["Types"]![0]!["Path"] = null);
Malformed("null proc entry rejected", j => j["Procs"]![0] = null);
Malformed("null proc name rejected", j => j["Procs"]![0]!["Name"] = null);
Malformed("null procedure chain rejected", j => j["Types"]![1]!["Procs"]![0] = null);
Malformed("null string entry rejected", j => j["Strings"]![0] = null);
Malformed("null argument entry rejected", j => j["Procs"]![3]!["Arguments"]![0] = null);
Malformed("null argument name rejected", j => j["Procs"]![3]!["Arguments"]![0]!["Name"] = null);
Malformed("null source map rejected", j => j["Procs"]![1]!["SourceInfo"] = null);
Malformed("null source entry rejected", j => j["Procs"]![1]!["SourceInfo"]![0] = null);
Malformed("null global names collection rejected", j => j["Globals"]!["Names"] = null);
Malformed("null global values collection rejected", j => j["Globals"]!["Globals"] = null);
Malformed("null global name rejected", j => j["Globals"]!["Names"]![0] = null);
Malformed("null map entry rejected", j => j["Maps"]![0] = null);
Malformed("null map definitions rejected", j => j["Maps"]![0]!["CellDefinitions"] = null);
Malformed("null map cell definition rejected", j => j["Maps"]![0]!["CellDefinitions"]!["a"] = null);
Malformed("null map objects collection rejected", j => j["Maps"]![0]!["CellDefinitions"]!["a"]!["Objects"] = null);
Malformed("null map object rejected", j => j["Maps"]![0]!["CellDefinitions"]!["c"]!["Objects"]![0] = null);
Malformed("null map blocks collection rejected", j => j["Maps"]![0]!["Blocks"] = null);
Malformed("null map block rejected", j => j["Maps"]![0]!["Blocks"]![0] = null);
Malformed("null map block cells rejected", j => j["Maps"]![0]!["Blocks"]![0]!["Cells"] = null);
Malformed("null map block cell key rejected", j => j["Maps"]![0]!["Blocks"]![0]!["Cells"]![0] = null);
Malformed("null tagged type reference rejected", j => j["Types"]![1]!["Variables"]!["turf"]!["value"] = null);
Malformed("null tagged value kind rejected", j => j["Types"]![1]!["Variables"]!["turf"]!["type"] = null);
Malformed("null tagged resource path rejected", j => {
    var turf = j["Types"]!.AsArray().First(t => t!["Path"]!.GetValue<string>() == "/turf")!;
    turf["Variables"]!["icon"]!["resourcePath"] = null;
});
tests.Add(("map area overrides rejected rather than ignored", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    json["Maps"]![0]!["CellDefinitions"]!["a"]!["Area"]!["VarOverrides"] = new JsonObject { ["name"] = "custom area" };
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "area variable overrides");
}));

var semanticsPath = args.Length > 1 ? args[1] : "build/semantics.json";
var optimizedPath = args.Length > 2 ? args[2] : "build/semantics-optimized.json";
void Semantics(string name, string proc, string[] immediate, string[] afterOne, bool optimized = false) {
    tests.Add((name, () => {
        var runtime = new DmRuntime(CompiledProject.Load(optimized ? optimizedPath : semanticsPath));
        runtime.Boot(); runtime.InvokeGlobal(proc);
        Check(runtime.Logs.Select(x => x.Text).SequenceEqual(immediate), "Immediate logs: " + string.Join(", ", runtime.Logs.Select(x => x.Text)));
        runtime.AdvanceBy(1);
        Check(runtime.Logs.Select(x => x.Text).SequenceEqual(afterOne), "Delayed logs: " + string.Join(", ", runtime.Logs.Select(x => x.Text)));
        Check(runtime.PendingThreads == 0, "Continuation leaked.");
    }));
}
Semantics("waitfor=0 detaches only when callee sleeps", "test_waitfor",
    ["caller-start", "worker-start", "caller-after"], ["caller-start", "worker-start", "caller-after", "worker-end"]);
Semantics("spawn copies local bindings", "test_spawn_copy",
    ["parent-local=2"], ["parent-local=2", "spawn-local=1"]);
Semantics("spawn retains shared list references", "test_shared_list", [], ["spawn-list=2"]);
Semantics("zero-delay scheduler preserves FIFO continuation order", "test_zero_delay",
    ["before-zero", "spawn-zero", "after-zero"], ["before-zero", "spawn-zero", "after-zero"]);
Semantics("negative spawn executes before caller continues", "test_negative_spawn",
    ["spawn-negative", "after-negative"], ["spawn-negative", "after-negative"]);
Semantics("DM arithmetic and branch execution", "test_arithmetic", ["arithmetic-ok"], ["arithmetic-ok"]);

Semantics("integer modulo truncates floats and handles signed minimum", "test_integer_modulo", ["mod=1", "mod=-1", "mod=0"], ["mod=1", "mod=-1", "mod=0"]);
Semantics("static world fields and implemented time remain readable", "test_world_static", ["time=0, fps=10"], ["time=0, fps=10"]);
void SemanticFailure(string name, string proc, string error) {
    tests.Add((name, () => {
        var runtime = new DmRuntime(CompiledProject.Load(semanticsPath)); runtime.Boot();
        Throws(() => runtime.InvokeGlobal(proc), error);
    }));
}
SemanticFailure("fractional divisor coercing to zero raises DM diagnostic", "test_fractional_modulo_error", "Modulo divisor becomes zero after integer coercion");
SemanticFailure("modulo failure retains source location", "test_fractional_modulo_error", "semantics.dm:");
SemanticFailure("recursive negative spawn is bounded without host stack overflow", "test_negative_spawn_recursion", "Immediate spawn depth limit");
SemanticFailure("world.tick_usage placeholder is rejected", "test_world_tick_usage", "world.tick_usage is not implemented");
SemanticFailure("world.contents placeholder is rejected", "test_world_contents", "world.contents is not implemented");
SemanticFailure("world.realtime placeholder is rejected", "test_world_realtime", "world.realtime is not implemented");

Semantics("optimized literal lists preserve order and duplicates", "test_literal_lists",
    ["numbers=3:3,1,3", "strings=3:third,first,third", "resources=2:../tests/DmGodot.Runtime.Tests/Fixtures/semantics.dm,../tests/DmGodot.Runtime.Tests/Fixtures/semantics.dme"],
    ["numbers=3:3,1,3", "strings=3:third,first,third", "resources=2:../tests/DmGodot.Runtime.Tests/Fixtures/semantics.dm,../tests/DmGodot.Runtime.Tests/Fixtures/semantics.dme"], optimized: true);
Semantics("optimized reference lists preserve shared identity", "test_reference_lists",
    ["references=2:7,7", "shared=9,9"], ["references=2:7,7", "shared=9,9"], optimized: true);

tests.Add(("pinned optimizer emits numeric, string and resource list constructors", () => {
    var json = JsonNode.Parse(File.ReadAllText(optimizedPath))!;
    foreach (var (proc, opcode) in new[] {
        ("literal_numbers", DreamProcOpcode.CreateListNFloats),
        ("literal_strings", DreamProcOpcode.CreateListNStrings),
        ("literal_resources", DreamProcOpcode.CreateListNResources)
    }) {
        var definition = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == proc)!;
        var code = Convert.FromBase64String(definition["Bytecode"]!.GetValue<string>());
        Check(code.Length > 0 && code[0] == (byte)opcode, $"Expected {proc} to start with {opcode}; bytecode={Convert.ToHexString(code)}.");
    }
}));

tests.Add(("reference list instruction preserves shared identity", () => {
    // The pinned optimizer leaves argument-reference lists as individual pushes.
    // Exercise its supported wire instruction explicitly in the compiled fixture.
    var json = JsonNode.Parse(File.ReadAllText(optimizedPath))!;
    var definition = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "literal_refs")!;
    byte[] code = [(byte)DreamProcOpcode.CreateListNRefs, 2, 0, 0, 0,
        8, 0, 8, 1, (byte)DreamProcOpcode.Return];
    definition["Bytecode"] = Convert.ToBase64String(code);
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    runtime.Boot();
    runtime.InvokeGlobal("test_reference_lists");
    Check(runtime.Logs.Select(x => x.Text).SequenceEqual(new[] {
        "references=2:7,7", "shared=9,9"
    }), "Reference list construction copied or reordered shared values.");
    Check(runtime.PendingThreads == 0, "Continuation leaked.");
}));

Semantics("datum init inheritance, New args and instance identity", "test_datum_initialization",
    ["init=1,2,3,4", "args=5,9", "lists=7,1", "distinct=1"],
    ["init=1,2,3,4", "args=5,9", "lists=7,1", "distinct=1"]);
Semantics("sleeping New suspends construction and caller", "test_sleeping_constructor",
    ["caller-start", "new-start"], ["caller-start", "new-start", "new-end", "caller-result=7"]);
Semantics("waitfor zero New returns object at suspension", "test_detached_constructor",
    ["detached-start", "detached-result=1"], ["detached-start", "detached-result=1", "detached-end", "detached-later=2"]);
Semantics("nested construction retains continuation across sleep", "test_nested_constructor",
    ["new-start"], ["new-start", "new-end", "nested-result=11"]);
Semantics("dynamic type reference construction preserves arguments", "test_dynamic_datum",
    ["dynamic=6"], ["dynamic=6"]);
Semantics("plain datum construction returns object", "test_plain_datum", ["plain=1"], ["plain=1"]);
SemanticFailure("native atom allocation remains explicit", "test_native_creation_error", "native/atom type");
SemanticFailure("construction errors retain DM source", "test_native_creation_error", "semantics.dm:");
SemanticFailure("recursive constructors have bounded frames", "test_constructor_depth", "call depth limit");

SemanticFailure("native datum allocation remains explicit", "test_native_datum_error", "native/atom type");

Semantics("datum parent_type reflects inheritance", "test_datum_parent_type", ["parent=1"], ["parent=1"]);
SemanticFailure("datum vars view is explicit", "test_datum_vars_error", "datum.vars is not implemented");
SemanticFailure("datum parent_type cannot be changed", "test_datum_parent_write_error", "Mutation of datum.parent_type");

Semantics("child without own initializer inherits initialization once", "test_inherited_initializer",
    ["inherited=1,3"], ["inherited=1,3"]);
Semantics("detached outer New retains sleeping nested construction", "test_detached_nested_constructor",
    ["new-start", "outer-returned=1"], ["new-start", "outer-returned=1", "new-end", "outer-done", "outer-later=12"]);

Semantics("instance calls dispatch by receiver and preserve argument order", "test_instance_dispatch",
    ["override=145", "relay=23", "receivers=45,23", "identity=1,1", "usr=1"],
    ["override=145", "relay=23", "receivers=45,23", "identity=1,1", "usr=1"]);
Semantics("sleeping method retains receiver and caller continuation", "test_sleeping_method",
    ["method-start=0"], ["method-start=0", "method-end=6", "method-result=6", "method-caller=6"]);
Semantics("detached method retains receiver after returning to caller", "test_detached_method",
    ["method-detached-start", "method-caller=0"],
    ["method-detached-start", "method-caller=0", "method-detached-end=9", "method-later=9"]);
SemanticFailure("null method receiver is explicit", "test_null_method", "null or non-object receiver");
SemanticFailure("missing instance method is explicit", "test_missing_method", "no procedure");
SemanticFailure("native instance method remains explicit", "test_native_method", "Native proc");
SemanticFailure("method failures retain source location", "test_null_method", "semantics.dm:");
SemanticFailure("recursive instance methods are bounded", "test_method_depth", "call depth limit");

SemanticFailure("non-object method receiver is explicit", "test_nonobject_method", "null or non-object receiver");
SemanticFailure("named method arguments remain explicit", "test_named_method_error", "argument mode");

var failed = 0;
foreach (var (name, run) in tests) {
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} runtime tests passed.");
return failed == 0 ? 0 : 1;
