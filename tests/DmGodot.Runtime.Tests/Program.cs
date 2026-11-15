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
tests.Add(("non-initializer procedure on mapped movable fails preflight", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var mappedType = json["Types"]!.AsArray().First(t => t!["Path"]!.GetValue<string>() == "/mob/player")!;
    mappedType["InitProc"] = 1;
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Movable initializer");
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

Semantics("empty user datum methods return null through instance, self and super calls", "test_empty_methods",
    ["empty=1,1,1"], ["empty=1,1,1"]);
SemanticFailure("inherited native stub is not treated as empty user method", "test_inherited_native_method", "Native proc");


tests.Add(("map retains turf, shared area and mapped object identity", () => {
    var runtime = new DmRuntime(CompiledProject.Load(path));
    Check(runtime.MapCells.Count == 25, "Expected retained map cell for each turf.");
    Check(runtime.MapCells.Select(c => c.Turf).Distinct().Count() == 25, "Turfs must be distinct objects.");
    Check(runtime.MapCells.Select(c => c.Area).Distinct().Count() == 1, "Identical area prototypes must share an area.");
    var cell = runtime.MapCells.Single(c => c.X == 3 && c.Y == 3);
    var entity = cell.Entities.Single();
    Check(entity.TypePath == "/mob/player", "Mapped entity identity type mismatch.");
    runtime.Boot(); runtime.AdvanceBy(2);
    var start = runtime.Logs.Count;
    runtime.InvokeGlobal("test_mapped_instance", new(entity), new(cell.Turf), new(cell.Area));
    runtime.InvokeGlobal("test_mapped_instance", new(entity), new(cell.Turf), new(cell.Area));
    Check(runtime.Logs.Skip(start).Select(l => l.Text).SequenceEqual(new[] {
        "links=1,1,1", "coordinates=3,3,1", "visits=1",
        "links=1,1,1", "coordinates=3,3,1", "visits=2"
    }), "Mapped DM reads or persistent method state mismatch.");
    Check(ReferenceEquals(entity, runtime.MapCells.Single(c => c.X == 3 && c.Y == 3).Entities.Single()), "Mapped identity changed.");
}));
tests.Add(("repeated mapped entities are distinct instances", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var objects = json["Maps"]![0]!["CellDefinitions"]!["c"]!["Objects"]!.AsArray();
    objects.Add(objects[0]!.DeepClone());
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    var cell = runtime.MapCells.Single(c => c.X == 3 && c.Y == 3);
    Check(cell.Entities.Count == 2 && !ReferenceEquals(cell.Entities[0], cell.Entities[1]), "Mapped entities were coalesced.");
    runtime.Boot(); runtime.AdvanceBy(2);
    runtime.InvokeGlobal("test_mapped_instance", new(cell.Entities[0]), new(cell.Turf), new(cell.Area));
    Check(cell.Entities[0].Variables["map_visits"].Number == 1 && cell.Entities[1].Variables["map_visits"].Number == 0, "Instance state leaked.");
}));
tests.Add(("default area is retained and area coordinates follow lowest turf", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    foreach (var cell in json["Maps"]![0]!["CellDefinitions"]!.AsObject())
        cell.Value!["Area"] = null;
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    var region = runtime.MapCells[0].Area;
    Check(region.TypePath == "/area" && runtime.MapCells.All(c => ReferenceEquals(c.Area, region)), "world.area fallback was not shared.");
    runtime.Boot(); runtime.AdvanceBy(2);
    runtime.InvokeGlobal("test_map_coordinates", new DmValue(region));
    Check(runtime.Logs[^1].Text == "coordinates=1,1,1", "Area coordinates must come from lowest turf.");
}));
tests.Add(("different area types retain distinct identities and coordinates", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var areaId = json["Types"]!.AsArray().Select((type, id) => (type, id)).Single(p => p.type!["Path"]!.GetValue<string>() == "/area").id;
    json["Maps"]![0]!["CellDefinitions"]!["a"]!["Area"]!["Type"] = areaId;
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    Check(runtime.MapCells.Select(c => c.Area).Distinct().Count() == 2, "Different areas coalesced.");
    var station = runtime.MapCells.First(c => c.Area.TypePath == "/area/station").Area;
    runtime.Boot(); runtime.AdvanceBy(2); runtime.InvokeGlobal("test_map_coordinates", new DmValue(station));
    Check(runtime.Logs[^1].Text == "coordinates=2,2,1", "Area coordinate order mismatch.");
}));
void MapFailure(string name, Action<JsonNode> mutate, string error) {
    tests.Add((name, () => {
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        mutate(json);
        Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), error);
    }));
}
int TypeId(JsonNode json, string typePath) => json["Types"]!.AsArray().Select((type, id) => (type, id)).Single(p => p.type!["Path"]!.GetValue<string>() == typePath).id;
MapFailure("non-turf cell type rejected", j => j["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["Type"] = TypeId(j, "/mob/player"), "Map turf must");
MapFailure("non-area cell type rejected", j => j["Maps"]![0]!["CellDefinitions"]!["a"]!["Area"]!["Type"] = TypeId(j, "/turf/floor"), "Map area must");
MapFailure("non-movable mapped entity rejected", j => j["Maps"]![0]!["CellDefinitions"]!["c"]!["Objects"]![0]!["Type"] = TypeId(j, "/area/station"), "Map entity must");
MapFailure("managed turf overrides are rejected", j => j["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["VarOverrides"] = new JsonObject { ["loc"] = null }, "Map turf override");
MapFailure("malformed area initializer linkage is rejected", j => {
    foreach (var cell in j["Maps"]![0]!["CellDefinitions"]!.AsObject()) cell.Value!["Area"] = null;
    j["Types"]![TypeId(j, "/area")]!["InitProc"] = 1;
}, "Area initializer");
void MapFieldFailure(string proc, string error) {
    tests.Add((error, () => {
        var runtime = new DmRuntime(CompiledProject.Load(path));
        runtime.Boot(); runtime.AdvanceBy(2);
        var entity = runtime.MapCells.Single(c => c.X == 3 && c.Y == 3).Entities.Single();
        Throws(() => runtime.InvokeGlobal(proc, new DmValue(entity)), error);
    }));
}
MapFieldFailure("test_map_contents", "Map atom.contents is not implemented");
MapFieldFailure("test_map_overlays", "Map atom.overlays is not implemented");
MapFieldFailure("test_map_loc_write", "Mutation of map atom.loc");
MapFieldFailure("test_map_coordinate_write", "Mutation of map atom.x");
MapFieldFailure("test_map_density_write", "Mutation of map atom.density");
MapFieldFailure("test_map_color_write", "Mutation of map atom.color");

MapFieldFailure("test_map_color_read", "Map atom.color is not implemented");
MapFieldFailure("test_map_bounds_read", "Map atom.bound_width is not implemented");
MapFieldFailure("test_map_client_read", "Map atom.client is not implemented");
MapFieldFailure("test_map_sight_write", "Mutation of map atom.sight");

MapFieldFailure("test_map_output", "Map atom stringification is not implemented");

MapFailure("empty area override prototypes are rejected", j => j["Maps"]![0]!["CellDefinitions"]!["a"]!["Area"]!["VarOverrides"] = new JsonObject(), "Map area variable overrides");

tests.Add(("multi-level area identity survives while coordinates fail explicitly", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var map = json["Maps"]![0]!;
    map["MaxZ"] = 2;
    var secondLevel = map["Blocks"]![0]!.DeepClone();
    secondLevel["Z"] = 2;
    map["Blocks"]!.AsArray().Add(secondLevel);
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    Check(runtime.MapCells.Count == 50, "Second map level missing.");
    Check(runtime.MapCells.Select(c => c.Area).Distinct().Count() == 1, "Area identity must remain shared across levels.");
    var cell = runtime.MapCells.Single(c => c.X == 3 && c.Y == 3 && c.Z == 2);
    runtime.Boot(); runtime.AdvanceBy(2);
    var start = runtime.Logs.Count;
    runtime.InvokeGlobal("test_mapped_instance", new(cell.Entities.Single()), new(cell.Turf), new(cell.Area));
    Check(runtime.Logs.Skip(start).Select(l => l.Text).SequenceEqual(new[] {
        "links=1,1,1", "coordinates=3,3,2", "visits=1"
    }), "Multilevel location links must remain readable.");
    Throws(() => runtime.InvokeGlobal("test_map_coordinates", new DmValue(cell.Area)), "Coordinates for multi-level map areas");
}));


tests.Add(("DM coordinate lookup retains turf identity and method state", () => {
    var runtime = new DmRuntime(CompiledProject.Load(path));
    runtime.Boot(); runtime.AdvanceBy(2);
    var start = runtime.Logs.Count;
    runtime.InvokeGlobal("test_locate_identity");
    Check(runtime.Logs.Skip(start).Select(l => l.Text).SequenceEqual(new[] {
        "same=1", "coordinates=2,3,1", "visits=1,2"
    }), "Located turf identity, coordinates or instance method state mismatch.");
    var expected = runtime.MapCells.Single(c => c.X == 2 && c.Y == 3 && c.Z == 1).Turf;
    Check(expected.Variables["lookup_visits"].Number == 2, "Located object was recreated instead of retained.");
    var mob = runtime.MapCells.Single(c => c.X == 3 && c.Y == 3 && c.Z == 1).Entities.Single();
    runtime.InvokeGlobal("test_locate_mapped_loc", new DmValue(mob));
    Check(runtime.Logs[^1].Text == "located-loc=1", "Locate result differs from mapped movable loc.");
}));
tests.Add(("coordinate lookup truncates fractions and rejects out-of-bounds coordinates", () => {
    var runtime = new DmRuntime(CompiledProject.Load(path));
    runtime.Boot(); runtime.AdvanceBy(2);
    void Locate(DmValue x, DmValue y, DmValue z, DmObject? expected) {
        runtime.InvokeGlobal("test_locate_coordinates", x, y, z);
        Check(ReferenceEquals(runtime.GetGlobal("located_turf").Value, expected), "Coordinate lookup result mismatch.");
    }
    var origin = runtime.MapCells.Single(c => c.X == 1 && c.Y == 1).Turf;
    var corner = runtime.MapCells.Single(c => c.X == 5 && c.Y == 5).Turf;
    var asymmetric = runtime.MapCells.Single(c => c.X == 2 && c.Y == 3).Turf;
    Locate(1, 1, 1, origin);
    Locate(5, 5, 1, corner);
    Locate(2.9f, 3.9f, 1.9f, asymmetric);
    Locate(5.9f, 5.9f, 1.9f, corner);
    foreach (var coordinates in new (float X, float Y, float Z)[] {
        (0, 1, 1), (1, 0, 1), (1, 1, 0), (-1, 1, 1), (1, -1, 1), (1, 1, -1),
        (6, 1, 1), (1, 6, 1), (1, 1, 2), (0.9f, 1, 1), (1, -0.9f, 1), (1, 1, 0.9f),
        (int.MinValue, 1, 1), (2147483520f, 1, 1)
    }) Locate(coordinates.X, coordinates.Y, coordinates.Z, null);
    Locate("1", 1, 1, null);
    Locate(1, DmValue.Null, 1, null);
    Locate(1, 1, new DmValue(origin), null);
    Locate(new DmValue(new DmTypeReference(origin.TypeId, origin.TypePath)), 1, 1, null);
}));
tests.Add(("coordinate lookup distinguishes z levels and returns null without a map", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var map = json["Maps"]![0]!;
    map["MaxZ"] = 2;
    var secondLevel = map["Blocks"]![0]!.DeepClone();
    secondLevel["Z"] = 2;
    map["Blocks"]!.AsArray().Add(secondLevel);
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    runtime.Boot(); runtime.AdvanceBy(2);
    runtime.InvokeGlobal("test_locate_coordinates", 2, 3, 2);
    var expected = runtime.MapCells.Single(c => c.X == 2 && c.Y == 3 && c.Z == 2).Turf;
    Check(ReferenceEquals(runtime.GetGlobal("located_turf").Value, expected), "Z coordinate was ignored.");
    Check(!ReferenceEquals(expected, runtime.MapCells.Single(c => c.X == 2 && c.Y == 3 && c.Z == 1).Turf), "Levels share a turf.");
    runtime.InvokeGlobal("test_locate_coordinates", 2, 3, 3);
    Check(runtime.GetGlobal("located_turf").IsNull, "Above-world z must return null.");
    json["Maps"] = new JsonArray();
    var empty = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    empty.Boot(); empty.AdvanceBy(2);
    empty.InvokeGlobal("test_locate_coordinates", 1, 1, 1);
    Check(empty.GetGlobal("located_turf").IsNull, "Empty map must return null.");
}));

tests.Add(("unsupported coordinate casts produce source diagnostics", () => {
    foreach (var coordinates in new (float X, float Y, float Z)[] {
        (float.MaxValue, 1, 1), (1, float.MinValue, 1), (1, 1, float.PositiveInfinity),
        (float.NaN, 1, 1), (1, float.NegativeInfinity, 1), (2147483648f, 1, 1)
    }) {
        var runtime = new DmRuntime(CompiledProject.Load(path));
        runtime.Boot(); runtime.AdvanceBy(2);
        try {
            runtime.InvokeGlobal("test_locate_coordinates", coordinates.X, coordinates.Y, coordinates.Z);
            throw new Exception("Expected unsupported coordinate cast.");
        } catch (CompatibilityException ex) {
            Check(ex.Message.Contains("finite signed 32-bit integer range"), "Missing coordinate range diagnostic.");
            Check(ex.Message.Contains("world.dm:"), "Missing DM source location.");
        }
    }
}));

var mapNewPath = args.Length > 3 ? args[3] : "build/map-new.json";
tests.Add(("static map New roots preserve phase order, inherited callbacks and suspension", () => {
    var runtime = new DmRuntime(CompiledProject.Load(mapNewPath));
    runtime.Boot();
    var roots = new[] { "area-a", "area-b", "area-unused", "turf-2,2", "turf-1,2", "turf-2,1", "turf-1,1",
        "obj-1,1", "obj-2,1", "obj-1,2", "obj-2,2", "world" };
    var prefixes = roots.Select(r => r.StartsWith("area-") ? r + ":1:1" : r.StartsWith("obj-") ? r + ":1" : r);
    Check(runtime.Logs.Select(l => l.Text).SequenceEqual(new[] { "global" }.Concat(prefixes).Concat(roots.Select(r => "spawn-" + r))),
        "Map callback prefixes/order or zero-delay scheduling changed: " + string.Join("|", runtime.Logs.Select(l => l.Text)));
    var unused = (DmObject)runtime.GetGlobal("unused_area").Value!;
    Check(!runtime.MapCells.Any(c => ReferenceEquals(c.Area, unused)), "Default area was not unused.");
    Check(new[] { "unused_x", "unused_y", "unused_z" }.All(n => runtime.GetGlobal(n).Number == 0), "Unused area coordinates must be zero.");
    var objects = runtime.MapCells.SelectMany(c => new[] { c.Area, c.Turf }.Concat(c.Entities)).Append(unused).Distinct().ToArray();
    Check(objects.All(o => o.Variables["new_count"].Number == 1), "New must run once per identity.");
    Check(runtime.MapCells.SelectMany(c => c.Entities).All(o => o.Variables["forwarding_count"].Number == 2), "Forwarding New prefixes did not run.");
    Check(runtime.PendingThreads == roots.Length, "Sleeping constructors must remain independent roots.");
    runtime.AdvanceBy(1);
    Check(runtime.Logs.TakeLast(roots.Length).Select(l => l.Text).SequenceEqual(roots.Select(r => "wake-" + r)), "Sleeping New wake order changed.");
    Check(runtime.PendingThreads == 0, "Map constructor continuations leaked.");
    Check(runtime.MapCells.SelectMany(c => c.Entities).All(o => o.Variables["forwarding_count"].Number == 4), "Forwarding New continuations did not resume.");
}));
tests.Add(("world executable initializer remains rejected", () => {
    var json = JsonNode.Parse(File.ReadAllText(mapNewPath))!;
    json["Types"]![TypeId(json, "/world")]!["InitProc"] = 1;
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Type initializers");
}));

tests.Add(("default area shares explicit identity and is skipped without levels", () => {
    var json = JsonNode.Parse(File.ReadAllText(mapNewPath))!;
    foreach (var definition in json["Maps"]![0]!["CellDefinitions"]!.AsObject())
        definition.Value!["Area"]!["Type"] = TypeId(json, "/area/region/unused");
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    runtime.Boot();
    var area = runtime.GetGlobal("unused_area").Value;
    Check(runtime.MapCells.All(c => ReferenceEquals(c.Area, area)), "Explicit default area must share its retained identity.");
    Check(runtime.Logs.Count(l => l.Text == "area-unused:1:1") == 1, "Default area callback duplicated.");
    json["Maps"] = new JsonArray();
    var empty = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    empty.Boot();
    Check(empty.GetGlobal("unused_area").IsNull, "Zero levels must not allocate a default area.");
    Check(empty.Logs.Select(l => l.Text).SequenceEqual(new[] { "global", "world", "spawn-world" }), "Empty map lifecycle changed.");
}));

tests.Add(("map New separates spatial turf order from block and cell object order", () => {
    var json = JsonNode.Parse(File.ReadAllText(mapNewPath))!;
    var map = json["Maps"]![0]!;
    foreach (var definition in map["CellDefinitions"]!.AsObject()) {
        var objects = definition.Value!["Objects"]!.AsArray();
        objects.Add(objects[0]!.DeepClone());
    }
    var first = map["Blocks"]![0]!.DeepClone();
    first["Z"] = 2;
    var second = map["Blocks"]![0]!.DeepClone();
    map["Blocks"] = new JsonArray(first, second); // construction order intentionally opposite z order
    map["MaxZ"] = 2;
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
    runtime.Boot();
    var ordered = runtime.MapCells.OrderByDescending(c => c.Z).ThenBy(c => c.Y).ThenBy(c => c.X).SelectMany(c => c.Entities).ToArray();
    Check(ordered.Select(o => o.Variables["new_order"].Number).SequenceEqual(Enumerable.Range(1, 16).Select(i => (float)i)),
        "Movables must retain block order, bottom-up rows and cell object order.");
    var turfs = runtime.MapCells.OrderBy(c => c.Z).ThenByDescending(c => c.Y).ThenByDescending(c => c.X).Select(c => c.Turf);
    Check(turfs.Select(o => o.Variables["new_order"].Number).SequenceEqual(Enumerable.Range(1, 8).Select(i => (float)i)),
        "Turf callbacks must use reverse XY per ascending z.");
    runtime.AdvanceBy(1);
    Check(ordered.All(o => o.Variables["forwarding_count"].Number == 4), "Multi-block constructor continuations did not complete.");
}));

var areaInitPath = args.Length > 4 ? args[4] : "build/area-init.json";
var areaInitOptimizedPath = args.Length > 5 ? args[5] : "build/area-init-optimized.json";
tests.Add(("area list initialization agrees across compiler modes with isolated inherited defaults", () => {
    string[]? reference = null;
    foreach (var artifact in new[] { areaInitPath, areaInitOptimizedPath }) {
        var runtime = new DmRuntime(CompiledProject.Load(artifact));
        var areas = runtime.MapCells.Select(c => c.Area).Distinct().ToArray();
        Check(areas.Length == 5 && ReferenceEquals(runtime.MapCells[0].Area, runtime.MapCells[1].Area), "Shared area prototype was allocated twice.");
        Check(areas.All(a => a.Variables["new_count"].Number == 0), "New ran during area initialization.");
        Check(areas.Select(a => a.Variables["empty"].Value).Distinct().Count() == 5, "Empty lists shared across area identities.");
        var firstList = (DmList)areas[0].Variables["items"].Value!;
        Check(firstList.Values[1].Value is DmList, "Nested list default missing.");
        var otherList = (DmList)areas[4].Variables["items"].Value!;
        Check(!ReferenceEquals(firstList, otherList) && !ReferenceEquals(firstList.Values[1].Value, otherList.Values[1].Value), "Inherited nested lists shared across distinct area types.");
        runtime.Boot();
        runtime.InvokeGlobal("check_area_lists");
        Check(otherList.Values[0].Number == 1, "Inherited list mutation leaked into another instance.");
        var logs = runtime.Logs.Select(l => l.Text).ToArray();
        Check(logs.SequenceEqual(new[] { "initial=1,3,1,7", "isolated=9,3", "new=1,1,1,1" }), "Area initialization result changed.");
        if (reference is not null) Check(logs.SequenceEqual(reference), "Optimized/unoptimized area initialization differs.");
        reference = logs;
    }
}));
foreach (var unsafeType in new[] { "/area/unsafe_helper", "/area/unsafe_sleep", "/area/unsafe_native", "/area/unsafe_read", "/area/unsafe_new", "/area/local/unsafe_child" })
    tests.Add(($"area initializer rejects unsupported effects: {unsafeType}", () => {
        foreach (var artifact in new[] { areaInitPath, areaInitOptimizedPath }) {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            foreach (var definition in json["Maps"]![0]!["CellDefinitions"]!.AsObject())
                definition.Value!["Area"]!["Type"] = TypeId(json, unsafeType);
            try {
                _ = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
                throw new Exception("Unsupported area initializer was accepted.");
            } catch (CompatibilityException ex) {
                Check(ex.Message.Contains("Area initializer") && ex.Message.Contains("area-init.dm:"), "Missing source-located area initializer diagnostic: " + ex.Message);
            }
        }
    }));

var turfNamePath = args.Length > 6 ? args[6] : "build/turf-name.json";
tests.Add(("turf overrides preserve raw definitions and normalize native names before New", () => {
    var project = CompiledProject.Load(turfNamePath);
    var runtime = new DmRuntime(project);
    runtime.Boot();
    Check(runtime.Logs.Select(l => l.Text).SequenceEqual(new[] {
        "8::3", "7::3", "6::3", "5:type-name:3", "4::3", "3::3",
        "2:mapped <b>name</b>:7", "1:mapped <b>name</b>:7"
    }), "Turf New saw incorrect normalized name or override value.");
    var cells = runtime.MapCells;
    Check(cells[2].Turf.Variables["name"].IsNull && cells[3].Turf.Variables["name"].Number == 42,
        "Native normalization destroyed raw null/numeric override values.");
    Check(cells[6].Turf.Variables["name"].Value is DmTypeReference && cells[7].Turf.Variables["name"].Value is DmResource,
        "Raw type/resource override values were dropped.");
    Check(!ReferenceEquals(cells[0].Turf, cells[1].Turf), "Repeated map prototype shared a turf.");
    runtime.InvokeGlobal("test_turf_override_isolation");
    Check(runtime.Logs.Last().Text == "markers=99,7", "Per-cell user override state leaked.");
    var second = new DmRuntime(project);
    second.Boot();
    Check(second.Logs.TakeLast(2).All(l => l.Text.EndsWith(":7")), "A runtime mutated shared project definition defaults.");
    Check(second.MapCells[4].Turf.Variables["name"].ToString() == "type-name", "Inherited type default was overwritten.");
}));
foreach (var (proc, error) in new[] {
    ("test_turf_name_write", "Mutation of map atom.name"),
    ("test_turf_name_output", "Map atom stringification")
}) tests.Add(($"{proc} remains unsupported", () => {
    var runtime = new DmRuntime(CompiledProject.Load(turfNamePath)); runtime.Boot();
    Throws(() => runtime.InvokeGlobal(proc), error);
}));
foreach (var field in new[] { "unknown_field", "tag", "type", "parent_type", "vars", "loc", "alpha" })
    tests.Add(($"turf override rejects {field}", () => {
        var json = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
        json["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["VarOverrides"] = new JsonObject { [field] = "bad" };
        Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Map turf override");
    }));
foreach (var kind in new[] { 2, 3 })
    tests.Add(($"turf name rejects unsupported compiler tag {kind}", () => {
        var json = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
        json["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["VarOverrides"] = new JsonObject {
            ["name"] = kind == 3 ? new JsonObject { ["type"] = 3, ["values"] = new JsonArray() } : new JsonObject { ["type"] = kind, ["value"] = 0 }
        };
        Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Compiler value kind");
    }));

tests.Add(("turf overrides do not bypass inherited executable initialization rejection", () => {
    var json = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
    json["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["Type"] = TypeId(json, "/turf/initialized_child");
    json["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["VarOverrides"] = new JsonObject { ["name"] = "override" };
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Type initializers are not implemented: /turf/initialized_base");
}));

tests.Add(("static turf layer preserves finite values and normalizes nonnumbers before New", () => {
    var runtime = new DmRuntime(CompiledProject.Load(turfNamePath)); runtime.Boot();
    Check(runtime.MapCells.Select(c => c.Turf.Variables["layer_seen"].Number)
        .SequenceEqual(new float[] { 4.75f, 4.75f, 0, -1, 2.5f, 0, 0, 0 }), "Layer default/override normalization changed.");
    Check(runtime.MapCells[5].Turf.Variables["layer"].Value is string &&
          runtime.MapCells[6].Turf.Variables["layer"].Value is DmTypeReference &&
          runtime.MapCells[7].Turf.Variables["layer"].Value is DmResource, "Raw layer overrides were destroyed.");
    Check(runtime.MapCells[2].Turf.Variables["layer"].IsNull, "Raw null layer was destroyed.");
    foreach (var value in new[] { float.MinValue, float.MaxValue, float.Epsilon, -0.25f, 0f, -1f, 123.75f }) {
        var json = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
        json["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["VarOverrides"]!["layer"] = value;
        var candidate = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); candidate.Boot();
        Check(candidate.MapCells[0].Turf.Variables["layer_seen"].Number == value, "Finite layer was clamped, rounded or remapped.");
        Check(candidate.MapCells[4].Turf.Variables["layer_seen"].Number == 2.5f, "Per-cell override mutated inherited layer defaults.");
    }
}));
tests.Add(("static turf layer preserves tagged infinities and final override precedence", () => {
    foreach (var kind in new[] { 4, 5 }) {
        var json = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
        json["Maps"]![0]!["CellDefinitions"]!["a"]!["Turf"]!["VarOverrides"]!["layer"] = new JsonObject { ["type"] = kind };
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var expected = kind == 4 ? float.PositiveInfinity : float.NegativeInfinity;
        Check(runtime.MapCells[0].Turf.Variables["layer_seen"].Number == expected, "Tagged infinity layer was changed.");
    }
    var inherited = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
    inherited["Types"]![TypeId(inherited, "/turf/base")]!["Variables"]!["layer"] = new JsonObject { ["type"] = 4 };
    var original = new DmRuntime(CompiledProject.Parse(inherited.ToJsonString())); original.Boot();
    Check(float.IsPositiveInfinity(original.MapCells[4].Turf.Variables["layer_seen"].Number), "Inherited infinity layer was changed.");
    inherited["Maps"]![0]!["CellDefinitions"]!["d"]!["Turf"]!["VarOverrides"] = new JsonObject { ["layer"] = 3.5f };
    var overridden = new DmRuntime(CompiledProject.Parse(inherited.ToJsonString())); overridden.Boot();
    Check(overridden.MapCells[4].Turf.Variables["layer_seen"].Number == 3.5f,
        "Map overrides must replace inherited values before New observes native layer.");
}));
foreach (var (proc, error) in new[] {
    ("test_turf_layer_write", "Mutation of map atom.layer"),
    ("test_area_layer_read", "Map atom.layer")
}) tests.Add(($"{proc} remains unsupported", () => {
    var runtime = new DmRuntime(CompiledProject.Load(turfNamePath)); runtime.Boot();
    Throws(() => runtime.InvokeGlobal(proc), error);
}));
tests.Add(("movable layer reads remain unsupported", () => {
    var json = JsonNode.Parse(File.ReadAllText(turfNamePath))!;
    json["Maps"]![0]!["CellDefinitions"]!["a"]!["Objects"] = new JsonArray(new JsonObject { ["Type"] = TypeId(json, "/mob") });
    var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
    var mob = runtime.MapCells[0].Entities.Single();
    Throws(() => runtime.InvokeGlobal("test_movable_layer_read", new DmValue(mob)), "Map atom.layer");
}));

tests.Add(("cached defaults keep public dictionaries independent across mutation and future allocations", () => {
    foreach (var artifact in new[] { semanticsPath, optimizedPath }) {
        var project = CompiledProject.Load(artifact);
        var runtime = new DmRuntime(project); runtime.Boot();
        DmObject Allocate() {
            runtime.InvokeGlobal("test_cache_allocate");
            return (DmObject)runtime.GetGlobal("cache_last").Value!;
        }
        var first = Allocate();
        var keys = first.Variables.Keys.ToArray();
        var firstList = (DmList)first.Variables["items"].Value!;
        var second = Allocate();
        var secondList = (DmList)second.Variables["items"].Value!;
        Check(!ReferenceEquals(first.Variables, second.Variables), "Instances share their public dictionary.");
        Check(!ReferenceEquals(firstList, secondList) && !ReferenceEquals(firstList.Values[1].Value, secondList.Values[1].Value),
            "Initializer-created nested lists were cached.");
        firstList.Values[0] = 99;
        first.Variables["scalar"] = 99;
        first.Variables.Remove("text_value");
        first.Variables["new_host_field"] = 42;
        first.Variables.Clear();
        Check(second.Variables["scalar"].Number == 7 && second.Variables["text_value"].ToString() == "parent" &&
              secondList.Values[0].Number == 1, "Mutating one instance changed another.");
        var third = Allocate();
        Check(third.Variables.Keys.SequenceEqual(keys), "Cache changed dictionary keys/order after host mutation.");
        Check(third.Variables["scalar"].Number == 7 && third.Variables["text_value"].ToString() == "parent", "Future allocation inherited mutated state.");
        Check(((DmTypeReference)third.Variables["type"].Value!).Path == "/datum/cache_parent/child" &&
              ((DmTypeReference)third.Variables["parent_type"].Value!).Path == "/datum/cache_parent", "Cached metadata changed.");
        Check(third.Variables["resource_value"].Value is DmResource && third.Variables["type_value"].Value is DmTypeReference,
            "Immutable resource/type defaults changed.");
        var separate = new DmRuntime(project); separate.Boot(); separate.InvokeGlobal("test_cache_allocate");
        var fourth = (DmObject)separate.GetGlobal("cache_last").Value!;
        Check(fourth.Variables["scalar"].Number == 7 && ((DmList)fourth.Variables["items"].Value!).Values[0].Number == 1,
            "A runtime mutated shared project defaults.");
    }
}));
tests.Add(("cached initializeLater defaults cannot bypass later executable initializer guards", () => {
    var runtime = new DmRuntime(CompiledProject.Load(areaInitPath));
    var typeId = runtime.MapCells[0].Area.TypeId; // constructor populated this cache via initializeLater=true
    var create = typeof(DmRuntime).GetMethod("CreateInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    try {
        create.Invoke(runtime, new object[] { typeId, false });
        throw new Exception("Warm defaults bypassed the initializer guard.");
    } catch (System.Reflection.TargetInvocationException ex) {
        Check(ex.InnerException is CompatibilityException && ex.InnerException.Message.Contains("Type initializers are not implemented: /area/local"),
            "Unexpected warm-cache initializer diagnostic.");
    }
}));
tests.Add(("cold default conversion preserves ancestor error order and rejected values", () => {
    var json = JsonNode.Parse(File.ReadAllText(path))!;
    var turf = json["Types"]![TypeId(json, "/turf")]!;
    var floor = json["Types"]![TypeId(json, "/turf/floor")]!;
    turf["Variables"]!["unsupported_default"] = new JsonObject { ["type"] = 2, ["value"] = 0 };
    floor["Variables"]!["unsupported_default"] = 0; // must not hide an unsupported ancestor value
    floor["InitProc"] = 1;
    foreach (var definition in json["Maps"]![0]!["CellDefinitions"]!.AsObject())
        definition.Value!["Turf"]!["Type"] = TypeId(json, "/turf/floor");
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Compiler value kind 2");
    turf["InitProc"] = 1;
    Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Type initializers are not implemented: /turf");
}));


tests.Add(("public object constructor remains empty with stable ordinal dictionary", () => {
    var instance = new DmObject(0, "/datum");
    var alias = instance.Variables;
    Check(alias.Count == 0 && ReferenceEquals(alias, instance.Variables), "Public constructor acquired metadata or unstable dictionary.");
    alias["same"] = 1; alias["Same"] = 2;
    Check(alias.Count == 2, "Public dictionary comparer changed.");
}));
tests.Add(("deferred defaults preserve metadata collisions and insertion order", () => {
    var ctor = typeof(DmObject).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
    var defaults = new System.Collections.ObjectModel.ReadOnlyDictionary<string, DmValue>(
        new Dictionary<string, DmValue>(StringComparer.Ordinal) {
            ["first"] = 1, ["parent_type"] = 99, ["middle"] = DmValue.Null, ["type"] = 98
        });
    var type = new DmValue(new DmTypeReference(2, "/datum/child"));
    var parent = new DmValue(new DmTypeReference(1, "/datum"));
    var instance = (DmObject)ctor.Invoke(new object[] { 2, "/datum/child", defaults, type, parent });
    var alias = instance.Variables;
    Check(alias.Keys.SequenceEqual(new[] { "first", "parent_type", "middle", "type" }), "Metadata override moved existing dictionary keys.");
    Check(alias["type"].Equals(type) && alias["parent_type"].Equals(parent) && alias.ContainsKey("middle") && alias["middle"].IsNull,
        "Metadata precedence or present-null field changed.");
    alias.Clear();
    Check(ReferenceEquals(alias, instance.Variables) && instance.Variables.Count == 0, "Clear resurrected defaults or metadata.");
    Check(defaults["type"].Number == 98 && defaults.Count == 4, "Materialization mutated defaults.");
}));
foreach (var artifact in new[] { semanticsPath, optimizedPath })
    tests.Add(($"dictionary exposure and internal writes preserve authoritative host aliases: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        DmObject Allocate() {
            runtime.InvokeGlobal("test_storage_allocate");
            return (DmObject)runtime.GetGlobal("storage_last").Value!;
        }
        var first = Allocate();
        var second = Allocate();
        runtime.InvokeGlobal("test_storage_read", new DmValue(first));
        Check(runtime.GetGlobal("storage_seen").Number == 7, "Unwritten default read changed.");
        runtime.InvokeGlobal("test_storage_write", new DmValue(first), 11);
        var alias = first.Variables;
        Check(alias["scalar"].Number == 11 && alias["nullable"].IsNull && alias.ContainsKey("nullable"), "First internal write lost defaults.");
        runtime.InvokeGlobal("test_storage_read", new DmValue(second));
        Check(runtime.GetGlobal("storage_seen").Number == 7, "First write affected another instance.");
        runtime.InvokeGlobal("test_storage_write", new DmValue(first), 12);
        Check(ReferenceEquals(alias, first.Variables) && alias["scalar"].Number == 12, "Internal write replaced an exposed dictionary.");
        alias["scalar"] = 23;
        runtime.InvokeGlobal("test_storage_read", new DmValue(first));
        Check(runtime.GetGlobal("storage_seen").Number == 23, "Host update was not visible to DM.");
        alias.Remove("scalar");
        Throws(() => runtime.InvokeGlobal("test_storage_read", new DmValue(first)), "Unknown field");
        Throws(() => runtime.InvokeGlobal("test_storage_write", new DmValue(first), 24), "Cannot assign field");
        alias.Remove("type");
        Throws(() => runtime.InvokeGlobal("test_storage_read_type", new DmValue(first)), "Unknown field");
        alias.Clear();
        alias["scalar"] = DmValue.Null;
        alias["extra"] = 31;
        runtime.InvokeGlobal("test_storage_read", new DmValue(first));
        Check(runtime.GetGlobal("storage_seen").IsNull, "Present-null field fell back to defaults.");
        runtime.InvokeGlobal("test_storage_read_extra", new DmValue(first));
        Check(runtime.GetGlobal("storage_seen").Number == 31 && alias.Count == 2 && ReferenceEquals(alias, first.Variables),
            "Host Clear/Add lost authority or resurrected metadata.");
        runtime.InvokeGlobal("test_storage_write", new DmValue(first), 32);
        Check(alias["scalar"].Number == 32 && !alias.ContainsKey("type"), "Write regenerated cleared metadata.");
        var third = Allocate();
        runtime.InvokeGlobal("test_storage_read", new DmValue(third));
        Check(runtime.GetGlobal("storage_seen").Number == 7 && second.Variables["scalar"].Number == 7,
            "Host mutation leaked to another or future object.");
    }));


foreach (var artifact in new[] { semanticsPath, optimizedPath }) {
    tests.Add(($"typesof follows actual parent preorder, concatenates duplicates and returns fresh lists: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var definitions = json["Types"]!.AsArray();
        var root = TypeId(json, "/datum/type_family");
        // Force an interleaved serialized-ID graph, independent of compiler ordering.
        var childIds = new[] { "/datum/type_family/zeta", "/datum/type_family/alpha",
            "/datum/type_family/zeta/deep", "/datum/type_elsewhere" }.Select(name => TypeId(json, name)).Order().ToArray();
        foreach (var id in childIds) definitions[id]!["Parent"] = root;
        definitions[childIds[^1]]!["Parent"] = childIds[0];
        int[] Preorder(int id) => new[] { id }.Concat(Enumerable.Range(0, definitions.Count)
            .Where(child => definitions[child]!["Parent"]?.GetValue<int>() == id)
            .SelectMany(Preorder)).ToArray();
        var expected = Preorder(root);
        var elsewhere = TypeId(json, "/datum/type_elsewhere");
        Check(expected.Contains(elsewhere) && !expected.SequenceEqual(expected.Order()), "Fixture must distinguish parent DFS from path prefixes and flat IDs.");
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var type = new DmValue(new DmTypeReference(root, "/datum/type_family"));
        DmList Result() => (DmList)runtime.GetGlobal("types_result").Value!;
        int[] Ids() => Result().Values.Select(value => ((DmTypeReference)value.Value!).Id).ToArray();
        runtime.InvokeGlobal("test_typesof", type);
        Check(Ids().SequenceEqual(expected), "Root-inclusive actual-parent DFS order changed.");
        var first = Result();
        runtime.InvokeGlobal("test_typesof", "/datum/type_family");
        Check(Ids().SequenceEqual(expected) && !ReferenceEquals(first, Result()), "String lookup or fresh list identity changed.");
        first.Values.Clear();
        Check(Ids().SequenceEqual(expected), "Mutating an earlier result changed a new result.");
        var instance = new DmObject(root, "/datum/type_family");
        instance.Variables["type"] = new DmValue(new DmTypeReference(elsewhere, "/datum/type_elsewhere"));
        runtime.InvokeGlobal("test_typesof", new DmValue(instance));
        Check(Ids().SequenceEqual(expected), "Object type identity was replaced by public field metadata.");
        runtime.InvokeGlobal("test_typesof_multiple", type, new DmValue(new DmTypeReference(elsewhere, "/datum/type_elsewhere")));
        Check(Ids().SequenceEqual(expected.Concat(Preorder(elsewhere)).Concat(expected)), "Argument order or duplicate retention changed.");
        runtime.InvokeGlobal("test_typesof_empty");
        var empty = Result();
        Check(empty.Values.Count == 0, "No-argument typesof should be empty.");
        runtime.InvokeGlobal("test_typesof_ignored", new DmValue(new DmList()));
        Check(Result().Values.Count == 0 && !ReferenceEquals(empty, Result()), "Primitive/list arguments must be ignored without sharing empty results.");
        runtime.InvokeGlobal("test_typesof", new DmValue(new DmTypeReference(0, definitions[0]!["Path"]!.GetValue<string>())));
        Check(Ids().SequenceEqual(Preorder(0)), "Root type identity must remain supported independently of root string lookup.");
    }));
    foreach (var value in new[] { "/", "", "/datum/type_family/", "/DATUM/type_family", "/missing_type" })
        tests.Add(($"typesof rejects unknown exact string {value}: {artifact}", () => {
            var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
            Throws(() => runtime.InvokeGlobal("test_typesof", value), "typesof unknown type path");
        }));
    foreach (var value in new[] { "/datum/type_family/proc", "/datum/type_family/verb" })
        tests.Add(($"typesof rejects unsupported proc/verb enumeration {value}: {artifact}", () => {
            var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
            Throws(() => runtime.InvokeGlobal("test_typesof", value), "typesof proc/verb");
        }));
    tests.Add(($"typesof retains strict-list and malformed identity guards: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        Throws(() => runtime.InvokeGlobal("test_typesof", new DmValue(new DmAssocList())), "strict associative");
        Throws(() => runtime.InvokeGlobal("test_typesof", new DmValue(new DmTypeReference(-1, "/invalid"))), "invalid type identity");
        Throws(() => runtime.InvokeGlobal("test_typesof", new DmValue(new DmObject(int.MaxValue, "/invalid"))), "invalid type identity");
        Throws(() => runtime.InvokeGlobal("test_typesof", new DmValue(new object())), "argument representation");
    }));
    tests.Add(($"typesof does not intercept an executable user procedure body: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var proc = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "typesof");
        proc!["Bytecode"] = Convert.ToBase64String(new byte[] { (byte)DreamProcOpcode.PushFloat, 0, 0, 224, 64, (byte)DreamProcOpcode.Return });
        proc["SourceInfo"] = new JsonArray();
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        runtime.InvokeGlobal("test_typesof", DmValue.Null);
        Check(runtime.GetGlobal("types_result").Number == 7, "Native dispatch replaced executable code.");
    }));
    tests.Add(($"typesof native work is instruction bounded before publishing results: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var root = TypeId(json, "/datum/type_family");
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())) { InstructionBudget = json["Types"]!.AsArray().Count - 1 };
        runtime.Boot();
        Throws(() => runtime.InvokeGlobal("test_typesof", new DmValue(new DmTypeReference(root, "/datum/type_family"))), "Instruction budget exhausted during typesof");
        Check(runtime.GetGlobal("types_result").IsNull, "Failed native work published a partial result.");
    }));
    tests.Add(($"typesof output lists share the public-boundary allocation budget: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var root = TypeId(json, "/datum/type_family");
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())) { InstructionBudget = 10_000_000 };
        runtime.Boot();
        var type = new DmValue(new DmTypeReference(root, "/datum/type_family"));
        Throws(() => runtime.InvokeGlobal("test_typesof_repeat", type, 0, 20_000), "List allocation budget");
        var last = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(last.Values.Count == 5, "Exhaustion partially published a result.");
        runtime.InvokeGlobal("test_typesof", type);
        Check(((DmList)runtime.GetGlobal("types_result").Value!).Values.Count == 5, "New public boundary failed to reset allocation budget.");
    }));
    tests.Add(($"typesof subtraction excludes the root without changing descendant order: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var type = new DmValue(new DmTypeReference(TypeId(json, "/datum/type_family"), "/datum/type_family"));
        runtime.InvokeGlobal("test_typesof", type);
        var expected = ((DmList)runtime.GetGlobal("types_result").Value!).Values.Skip(1).ToArray();
        runtime.InvokeGlobal("test_typesof_subtract", type);
        Check(((DmList)runtime.GetGlobal("types_result").Value!).Values.SequenceEqual(expected), "subtypesof root removal/order changed.");
    }));
}


foreach (var artifact in new[] { semanticsPath, optimizedPath }) {
    DmList Positional(params DmValue[] values) { var list = new DmList(); list.Values.AddRange(values); return list; }
    tests.Add(($"ordinary subtraction removes the last occurrence once per RHS entry: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        DmList Subtract(DmList a, DmValue b) {
            runtime.InvokeGlobal("test_list_subtract", new DmValue(a), b);
            return (DmList)runtime.GetGlobal("types_result").Value!;
        }
        var source = Positional(1, 2, 1, 3, 1);
        var result = Subtract(source, 1);
        Check(result.Values.SequenceEqual(new DmValue[] { 1, 2, 1, 3 }) && source.Values.Count == 5,
            "Scalar subtraction removed all/first occurrences or mutated source.");
        var rhs = Positional(1, 1, 99);
        rhs.Associations[1] = 3; // RHS associated values are never removals.
        result = Subtract(source, new DmValue(rhs));
        Check(result.Values.SequenceEqual(new DmValue[] { 1, 2, 3 }) && rhs.Values.Count == 3 && rhs.Associations[1].Number == 3,
            "RHS duplicates/order/association values were interpreted incorrectly.");
        var missing = Subtract(source, 99);
        Check(!ReferenceEquals(missing, source) && missing.Values.SequenceEqual(source.Values), "Absent subtraction must still copy.");
        var empty = Subtract(source, new DmValue(source));
        Check(empty.Values.Count == 0 && source.Values.Count == 5, "Self subtraction must empty the copy, not source.");
        var nulls = Positional(DmValue.Null, 0, DmValue.Null);
        result = Subtract(nulls, DmValue.Null);
        Check(result.Values.SequenceEqual(new DmValue[] { DmValue.Null, 0 }), "Null must not compare equal to numeric zero.");
        var numbers = Positional(new DmValue(float.NaN), 0, new DmValue(-0f), new DmValue(float.NaN));
        result = Subtract(numbers, new DmValue(float.NaN));
        Check(result.Values.Count == 3 && float.IsNaN(result.Values[0].Number), "NaN equality/last occurrence changed.");
        result = Subtract(numbers, 0);
        Check(result.Values.Count == 3 && result.Values[1].Number == 0 && float.IsNaN(result.Values[2].Number),
            "Signed zero must compare equal and remove only the last zero.");
    }));
    tests.Add(($"subtraction shallow-copies live associations and removes association with duplicate key: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        var nested = Positional(4);
        var obj = new DmObject(0, "/");
        var source = Positional("a", "b", "a", new DmValue(nested));
        source.Associations["a"] = new DmValue(nested);
        source.Associations["b"] = new DmValue(obj);
        source.Associations["orphan"] = 99;
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), "a");
        var result = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(result.Values.SequenceEqual(new DmValue[] { "a", "b", new(nested) }) && !result.Associations.ContainsKey("a"),
            "Removing duplicate associated key must delete association even when one key remains.");
        Check(!result.Associations.ContainsKey("orphan") && ReferenceEquals(result.Associations["b"].Value, obj),
            "Copy retained an orphan association or cloned an object.");
        Check(source.Associations.Count == 3 && source.Values.Count == 4, "Result removal changed source associations.");
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), new DmValue(new DmList()));
        var copy = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(copy.Associations.Count == 2 && ReferenceEquals(copy.Associations["a"].Value, nested) &&
            ReferenceEquals(copy.Values[^1].Value, nested), "Nested values must remain shared in a shallow copy.");
        copy.Associations["b"] = 5;
        nested.Values[0] = 6;
        Check(ReferenceEquals(source.Associations["b"].Value, obj) &&
            ((DmList)source.Associations["a"].Value!).Values[0].Number == 6, "Dictionary isolation or shallow value alias changed.");
    }));
    tests.Add(($"subtraction uses nested list/object/alist identity and expands only ordinary RHS lists: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        var nested = Positional(1); var equalNested = Positional(1);
        var first = new DmAssocList(); var second = new DmAssocList();
        first.Set("key", 1); second.Set("key", 1);
        var obj = new DmObject(0, "/"); var other = new DmObject(0, "/");
        var source = Positional(new(nested), new(equalNested), 1, new(first), new(second), new(first), new(obj), new(other));
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), new DmValue(nested));
        var result = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(result.Values.Count == 7 && ReferenceEquals(result.Values[0].Value, nested),
            "Ordinary RHS list must expand its positional contents, not remove its own identity.");
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), new DmValue(Positional(new(nested), new(obj))));
        result = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(!result.Values.Contains(new(nested)) && result.Values.Contains(new(equalNested)) &&
            !result.Values.Contains(new(obj)) && result.Values.Contains(new(other)), "Reference identity was replaced by structural equality.");
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), new DmValue(first));
        result = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(result.Values.Count == 7 && result.Values.Count(v => ReferenceEquals(v.Value, first)) == 1 &&
            result.Values.Contains(new(second)), "Strict RHS list must be one scalar identity, not enumerate keys.");
    }));
    tests.Add(($"subtraction preserves numeric behavior and rejects unsupported operators: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        runtime.InvokeGlobal("test_list_subtract", 7, 2);
        Check(runtime.GetGlobal("types_result").Number == 5, "Numeric subtraction changed.");
        runtime.InvokeGlobal("test_list_subtract", DmValue.Null, 2);
        Check(runtime.GetGlobal("types_result").Number == -2, "Null numeric subtraction changed.");
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(new DmAssocList()), 1), "Strict associative list subtraction");
        Throws(() => runtime.InvokeGlobal("test_list_subtract", 1, new DmValue(Positional(1))), "Expected a DM number");
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(new DmObject(0, "/")), 1), "Expected a DM number");
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(Positional(1)), new DmValue(new object())), "operand representation");
        var original = Positional(1, 2);
        Throws(() => runtime.InvokeGlobal("test_list_remove", new DmValue(original), 1), "not implemented");
        Check(original.Values.SequenceEqual(new DmValue[] { 1, 2 }), "Unsupported -= changed source.");
    }));
    tests.Add(($"subtraction charges search and shifting work before modifying copied results: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)) { InstructionBudget = 250 }; runtime.Boot();
        var source = Positional(Enumerable.Range(0, 100).Select(i => new DmValue((float)i)).ToArray());
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(source), 0), "Instruction budget exhausted during list subtraction");
        Check(runtime.GetGlobal("types_result").IsNull && source.Values.Count == 100, "Failed shift work published/mutated input.");
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), 99);
        Check(((DmList)runtime.GetGlobal("types_result").Value!).Values.Count == 99 && source.Values.Count == 100,
            "Cheap last removal failed or altered source.");
        var misses = Positional(Enumerable.Repeat(new DmValue(-1f), 100).ToArray());
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(source), new DmValue(misses)), "Instruction budget exhausted during list subtraction");
        Check(source.Values.Count == 100 && misses.Values.Count == 100, "Budget failure changed operands.");
    }));
    tests.Add(($"subtraction copies charge positional and unique live association slots: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)) { InstructionBudget = 1_000_000 }; runtime.Boot();
        var empty = new DmValue(new DmList());
        var source = Positional(Enumerable.Repeat(new DmValue(1f), 99_999).ToArray());
        source.Associations["orphan"] = 2; // Must not consume copied association slots.
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), empty);
        var success = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(success.Values.Count == 99_999 && success.Associations.Count == 0, "Exact positional budget/orphan pruning failed.");
        source.Values.Add(1);
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(source), empty), "List allocation budget");
        Check(ReferenceEquals(success, runtime.GetGlobal("types_result").Value), "Failed copy replaced published result.");
        var keyed = Positional(Enumerable.Repeat(new DmValue("key"), 99_997).ToArray());
        keyed.Associations["key"] = 4;
        runtime.InvokeGlobal("test_list_subtract", new DmValue(keyed), empty);
        success = (DmList)runtime.GetGlobal("types_result").Value!;
        Check(success.Values.Count == 99_997 && success.Associations.Count == 1, "Duplicate keys charged association more than once.");
        keyed.Values.Add("key");
        Throws(() => runtime.InvokeGlobal("test_list_subtract", new DmValue(keyed), empty), "List allocation budget");
        Check(ReferenceEquals(success, runtime.GetGlobal("types_result").Value) && keyed.Associations["key"].Number == 4,
            "Association budget failure published partial output or mutated source.");
    }));
    tests.Add(($"subtraction copy budgets aggregate within one public boundary: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)) { InstructionBudget = 1_000_000 }; runtime.Boot();
        var source = Positional(Enumerable.Repeat(new DmValue(1f), 60_000).ToArray());
        Throws(() => runtime.InvokeGlobal("test_list_subtract_twice", new DmValue(source)), "List allocation budget");
        Check(((DmList)runtime.GetGlobal("types_result").Value!).Values.Count == 60_000 && source.Values.Count == 60_000,
            "Second failed copy must preserve first result and input.");
        runtime.InvokeGlobal("test_list_subtract", new DmValue(source), DmValue.Null);
        Check(((DmList)runtime.GetGlobal("types_result").Value!).Values.Count == 60_000, "Public boundary failed to reset copy budget.");
    }));
}


foreach (var artifact in new[] { semanticsPath, optimizedPath }) {
    DmList EnumList(params DmValue[] values) { var list = new DmList(); list.Values.AddRange(values); return list; }
    tests.Add(($"enumeration preserves duplicates/null, keys and terminal null assignment: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        var list = EnumList(1, DmValue.Null, 1);
        runtime.InvokeGlobal("test_enum", new DmValue(list));
        Check(runtime.GetGlobal("enum_count").Number == 3 && runtime.GetGlobal("enum_last").IsNull &&
            runtime.Logs.Select(x => x.Text).SequenceEqual(new[] { "value=1", "value=", "value=1" }), "Positional snapshot/exhaustion changed.");
        var keyed = EnumList("key", "key", "plain");
        keyed.Associations["key"] = 99;
        runtime.InvokeGlobal("test_enum", new DmValue(keyed));
        Check(runtime.Logs.TakeLast(3).Select(x => x.Text).SequenceEqual(new[] { "value=key", "value=key", "value=plain" }),
            "Unfiltered enumeration must yield keys, not associated values.");
        foreach (var value in new DmValue[] { DmValue.Null, 5, "text", new(new DmTypeReference(0, "/")), new(new DmResource("unused")), new(new DmObject(0, "/")) }) {
            runtime.InvokeGlobal("test_enum", value);
            Check(runtime.GetGlobal("enum_count").Number == 0 && runtime.GetGlobal("enum_last").IsNull, "Ignored container did not create empty enumeration.");
        }
    }));
    tests.Add(($"enumeration snapshot survives source mutation and sleep with shallow references: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        var nested = EnumList(2);
        var source = EnumList(1, new(nested), 3);
        runtime.InvokeGlobal("test_enum_capture", new DmValue(source));
        Check(runtime.GetGlobal("enum_last").Number == 1 && runtime.PendingThreads == 1, "First enumeration suspension failed.");
        source.Values.Clear(); source.Values.Add(99); source.Associations["new"] = 100;
        nested.Values[0] = 7;
        runtime.AdvanceBy(1);
        Check(ReferenceEquals(runtime.GetGlobal("enum_last").Value, nested) && nested.Values[0].Number == 7,
            "Snapshot did not retain shallow nested reference.");
        runtime.AdvanceBy(1);
        Check(runtime.GetGlobal("enum_last").Number == 3 && runtime.GetGlobal("enum_count").Number == 3, "Mutation replaced remaining snapshot values.");
        runtime.AdvanceBy(1);
        Check(runtime.PendingThreads == 0 && source.Values[0].Number == 99, "Enumeration leaked thread or mutated input.");
    }));
    tests.Add(($"enumeration break/continue/return and callee slots remain independent: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        var source = new DmValue(EnumList(1, 2, 3));
        runtime.InvokeGlobal("test_enum_break", source);
        Check(runtime.GetGlobal("enum_last").Number == 2, "Break incorrectly exhausted/reset the current item.");
        runtime.InvokeGlobal("test_enum_continue", source);
        Check(runtime.Logs.TakeLast(2).Select(x => x.Text).SequenceEqual(new[] { "continue=1", "continue=3" }), "Continue recreated or lost cursor.");
        runtime.InvokeGlobal("test_enum_early", source);
        runtime.InvokeGlobal("test_enum_early", new DmValue(EnumList(8)));
        Check(runtime.GetGlobal("enum_last").Number == 8 && runtime.PendingThreads == 0, "Early return reused a prior frame cursor.");
        runtime.InvokeGlobal("test_enum_nested", new DmValue(EnumList(1, 2)));
        Check(runtime.Logs.TakeLast(6).Select(x => x.Text).SequenceEqual(new[] {
            "outer=1", "inner=1", "inner=2", "outer=2", "inner=1", "inner=2"
        }), "Callee enumerators changed caller cursor.");
        runtime.InvokeGlobal("test_enum_nested_same", new DmValue(EnumList(1, 2)));
        Check(runtime.Logs.TakeLast(4).Select(x => x.Text).SequenceEqual(new[] { "1:1", "1:2", "2:1", "2:2" }),
            "Nested loops in one frame shared a slot.");
    }));
    tests.Add(($"enumeration spawn captures locals and waitfor detachment preserves cursor: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        runtime.InvokeGlobal("test_enum_spawn", new DmValue(EnumList(1, 2)));
        Check(runtime.Logs.Select(x => x.Text).SequenceEqual(new[] { "parent=1", "parent=2", "spawn=1", "spawn=2" }),
            "Spawn changed parent cursor or copied local values incorrectly.");
        runtime.InvokeGlobal("test_enum_detached", new DmValue(EnumList(3, 4)));
        Check(runtime.Logs.TakeLast(2).Select(x => x.Text).SequenceEqual(new[] { "detached=3", "caller" }), "waitfor caller did not resume.");
        runtime.AdvanceBy(1);
        Check(runtime.Logs[^1].Text == "detached=4", "Detached frame lost enumerator cursor.");
        runtime.AdvanceBy(1);
        Check(runtime.PendingThreads == 0, "Detached enumerator frame leaked.");
    }));
    tests.Add(($"enumeration guards unsupported native/strict containers and invalid identities: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        Throws(() => runtime.InvokeGlobal("test_enum", new DmValue(new DmAssocList())), "Strict associative list enumeration");
        Throws(() => runtime.InvokeGlobal("test_enum", new DmValue(runtime.World)), "Native atom/world contents");
        Throws(() => runtime.InvokeGlobal("test_enum", new DmValue(new DmObject(TypeId(json, "/atom"), "/atom"))), "Native atom/world contents");
        Throws(() => runtime.InvokeGlobal("test_enum", new DmValue(new DmObject(-1, "/invalid"))), "invalid object type identity");
        Throws(() => runtime.InvokeGlobal("test_enum", new DmValue(new object())), "operand representation");
        Throws(() => runtime.InvokeGlobal("test_enum_filtered", new DmValue(EnumList())), "CreateFilteredListEnumerator");
    }));

    // Explicit wire cases cover compiler optimization/lifetime paths without
    // pretending malformed bytecode is normal source output.
    var enumJson = JsonNode.Parse(File.ReadAllText(artifact))!;
    var enumGlobal = Array.FindIndex(enumJson["Globals"]!["Names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(), n => n == "enum_last");
    DmRuntime EnumWire(List<byte> code, int budget = 1_000_000) {
        var json = enumJson.DeepClone();
        var proc = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "test_enum_wire")!;
        proc["Bytecode"] = Convert.ToBase64String(code.ToArray()); proc["SourceInfo"] = new JsonArray();
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())) { InstructionBudget = budget };
        runtime.Boot(); return runtime;
    }
    void EInt(List<byte> code, int value) => code.AddRange(BitConverter.GetBytes(value));
    void EOp(List<byte> code, DreamProcOpcode opcode) => code.Add((byte)opcode);
    void ESet(List<byte> code, float value) {
        EOp(code, DreamProcOpcode.PushFloat); code.AddRange(BitConverter.GetBytes(value));
        EOp(code, DreamProcOpcode.AssignNoPush); code.Add(10); EInt(code, enumGlobal);
    }
    void EEnd(List<byte> code) { EOp(code, DreamProcOpcode.PushNull); EOp(code, DreamProcOpcode.Return); }
    void ECreate(List<byte> code, int id) {
        EOp(code, DreamProcOpcode.PushReferenceValue); code.AddRange(new byte[] { 8, 0 });
        EOp(code, DreamProcOpcode.CreateListEnumerator); EInt(code, id);
    }
    int EEnumerate(List<byte> code, int id, bool assign = true, byte reference = 10) {
        EOp(code, assign ? DreamProcOpcode.Enumerate : DreamProcOpcode.EnumerateNoAssign); EInt(code, id);
        if (assign) { code.Add(reference); if (reference == 10) EInt(code, enumGlobal); }
        var patch = code.Count; EInt(code, 0); return patch;
    }
    void EPatch(List<byte> code, int patch, int target) {
        var bytes = BitConverter.GetBytes(target);
        for (var i = 0; i < 4; i++) code[patch + i] = bytes[i];
    }
    foreach (var mode in new[] { "missing", "empty", "destroyed" })
        tests.Add(($"enumeration {mode} slot preserves pinned terminal assignment: {artifact}", () => {
            var code = new List<byte>(); ESet(code, 99);
            if (mode != "missing") ECreate(code, 0);
            if (mode == "destroyed") {
                EOp(code, DreamProcOpcode.DestroyEnumerator); EInt(code, 0);
                EOp(code, DreamProcOpcode.DestroyEnumerator); EInt(code, 0);
            }
            var jump = EEnumerate(code, 0); ESet(code, 777);
            EPatch(code, jump, code.Count); EEnd(code);
            var runtime = EnumWire(code); runtime.InvokeGlobal("test_enum_wire", DmValue.Null);
            Check(mode == "empty" ? runtime.GetGlobal("enum_last").IsNull : runtime.GetGlobal("enum_last").Number == 99,
                "Missing slot/exhausted snapshot distinction changed.");
        }));
    tests.Add(($"enumeration exhaustion assigns through guarded references before failure jump: {artifact}", () => {
        var code = new List<byte>(); ECreate(code, 0);
        var jump = EEnumerate(code, 0, reference: 5); EPatch(code, jump, code.Count); EEnd(code);
        var runtime = EnumWire(code);
        Throws(() => runtime.InvokeGlobal("test_enum_wire", DmValue.Null), "Write reference kind 5");
    }));
    tests.Add(($"EnumerateNoAssign advances to exhaustion without writing: {artifact}", () => {
        var code = new List<byte>(); ESet(code, 0); ECreate(code, 15);
        var jumps = new List<int>();
        for (var i = 1; i <= 3; i++) { jumps.Add(EEnumerate(code, 15, assign: false)); ESet(code, i); }
        foreach (var jump in jumps) EPatch(code, jump, code.Count);
        EEnd(code);
        var runtime = EnumWire(code); runtime.InvokeGlobal("test_enum_wire", new DmValue(EnumList(1, 2)));
        Check(runtime.GetGlobal("enum_last").Number == 2, "NoAssign did not advance cursor or use slot 15.");
    }));
    tests.Add(($"spawned frame starts with empty enumerator slots: {artifact}", () => {
        var code = new List<byte>(); ESet(code, 99); ECreate(code, 0);
        EOp(code, DreamProcOpcode.PushFloat); code.AddRange(BitConverter.GetBytes(0f));
        EOp(code, DreamProcOpcode.Spawn); var parentJump = code.Count; EInt(code, 0);
        var childJump = EEnumerate(code, 0); ESet(code, 777);
        EPatch(code, childJump, code.Count); EEnd(code);
        EPatch(code, parentJump, code.Count); EEnd(code);
        var runtime = EnumWire(code); runtime.InvokeGlobal("test_enum_wire", new DmValue(EnumList(1)));
        Check(runtime.GetGlobal("enum_last").Number == 99 && runtime.PendingThreads == 0, "Spawn inherited/shared the parent's active enumerator.");
    }));
    foreach (var id in new[] { -1, 16 })
        foreach (var opcode in new[] { DreamProcOpcode.CreateListEnumerator, DreamProcOpcode.DestroyEnumerator, DreamProcOpcode.Enumerate, DreamProcOpcode.EnumerateNoAssign })
            tests.Add(($"enumeration rejects {opcode} slot {id}: {artifact}", () => {
                var code = new List<byte>();
                if (opcode == DreamProcOpcode.CreateListEnumerator) ECreate(code, id);
                else if (opcode is DreamProcOpcode.Enumerate or DreamProcOpcode.EnumerateNoAssign) {
                    var jump = EEnumerate(code, id, opcode == DreamProcOpcode.Enumerate); EPatch(code, jump, code.Count);
                } else { EOp(code, opcode); EInt(code, id); }
                EEnd(code);
                var runtime = EnumWire(code);
                Throws(() => runtime.InvokeGlobal("test_enum_wire", DmValue.Null), "slot must be in 0..15");
            }));

    tests.Add(($"unsupported associative/type/range enumerators remain guarded: {artifact}", () => {
        foreach (var opcode in new[] { DreamProcOpcode.EnumerateAssoc, DreamProcOpcode.CreateTypeEnumerator, DreamProcOpcode.CreateRangeEnumerator }) {
            var code = new List<byte>(); EOp(code, opcode); EInt(code, 0);
            if (opcode == DreamProcOpcode.EnumerateAssoc) {
                code.Add(10); EInt(code, enumGlobal); code.Add(10); EInt(code, enumGlobal);
                var jump = code.Count; EInt(code, 0); EPatch(code, jump, code.Count);
            }
            EEnd(code);
            var runtime = EnumWire(code);
            Throws(() => runtime.InvokeGlobal("test_enum_wire", DmValue.Null), $"Opcode {opcode}");
        }
    }));
    tests.Add(($"enumerator failure jumps require instruction boundaries: {artifact}", () => {
        var code = new List<byte>(); ECreate(code, 0);
        var jump = EEnumerate(code, 0); EPatch(code, jump, 1); EEnd(code);
        Throws(() => EnumWire(code), "instruction boundary");
    }));
    tests.Add(($"snapshot allocations aggregate and copy work is bounded: {artifact}", () => {
        var code = new List<byte>(); ECreate(code, 0); ECreate(code, 1); EEnd(code);
        var runtime = EnumWire(code);
        var source = EnumList(Enumerable.Repeat(new DmValue(1f), 60_000).ToArray());
        Throws(() => runtime.InvokeGlobal("test_enum_wire", new DmValue(source)), "List allocation budget");
        Check(source.Values.Count == 60_000 && runtime.GetGlobal("enum_last").IsNull, "Failed snapshot changed input or ran body.");
        code = []; ECreate(code, 0); EEnd(code);
        var cheap = EnumWire(code, 250);
        Throws(() => cheap.InvokeGlobal("test_enum_wire", new DmValue(source)), "Instruction budget exhausted during list enumeration snapshot");
        var boundary = EnumWire(code);
        boundary.InvokeGlobal("test_enum_wire", new DmValue(EnumList(Enumerable.Repeat(new DmValue(1f), 99_999).ToArray())));
        Throws(() => boundary.InvokeGlobal("test_enum_wire", new DmValue(EnumList(Enumerable.Repeat(new DmValue(1f), 100_000).ToArray()))), "List allocation budget");
        boundary.InvokeGlobal("test_enum_wire", new DmValue(EnumList(1)));
        Check(boundary.PendingThreads == 0, "Snapshot public-boundary reset failed.");
    }));
}


foreach (var artifact in new[] { semanticsPath, optimizedPath }) {
    JsonNode InitialJson() {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var index = Array.FindIndex(json["Globals"]!["Names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(), n => n == "initial_result");
        var code = new List<byte> {
            (byte)DreamProcOpcode.PushReferenceValue, 8, 0,
            (byte)DreamProcOpcode.PushReferenceValue, 8, 1,
            (byte)DreamProcOpcode.Initial, (byte)DreamProcOpcode.AssignNoPush, 10
        };
        code.AddRange(BitConverter.GetBytes(index));
        code.Add((byte)DreamProcOpcode.PushNull); code.Add((byte)DreamProcOpcode.Return);
        var proc = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "test_initial_wire")!;
        proc["Bytecode"] = Convert.ToBase64String(code.ToArray()); proc["SourceInfo"] = new JsonArray();
        return json;
    }
    tests.Add(($"initial preserves declared inheritance/null despite current instance mutation: {artifact}", () => {
        var json = InitialJson();
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        runtime.InvokeGlobal("test_initial_allocate");
        var instance = (DmObject)runtime.GetGlobal("initial_owner").Value!;
        Check(instance.Variables["items"].Value is DmList, "Fixture must have executed a per-instance list initializer.");
        instance.Variables["scalar"] = 99;
        runtime.InvokeGlobal("test_initial", new DmValue(instance));
        Check(runtime.GetGlobal("initial_result").Number == 7, "Initial read current instance state.");
        instance.Variables.Clear();
        instance.Variables["type"] = new DmValue(new DmTypeReference(0, "/"));
        instance.Variables["never_declared"] = 22;
        runtime.InvokeGlobal("test_initial", new DmValue(instance));
        Check(runtime.GetGlobal("initial_result").Number == 7, "Host Clear/metadata spoof changed original declared value.");
        var type = new DmValue(new DmTypeReference(instance.TypeId, instance.TypePath));
        runtime.InvokeGlobal("test_initial", type);
        Check(runtime.GetGlobal("initial_result").Number == 7, "Type owner did not use inherited declarations.");
        foreach (var key in new[] { "nullable", "items", "never_declared" }) {
            runtime.InvokeGlobal("test_initial_wire", new DmValue(instance), key);
            Check(runtime.GetGlobal("initial_result").IsNull, "Null placeholder/child null/missing field used current state or ancestor fallback.");
        }
        runtime.InvokeGlobal("test_initial_wire", type, "type_value");
        Check(runtime.GetGlobal("initial_result").Value is DmTypeReference value && value.Path == "/datum/initial_parent", "Inherited type default changed.");
        runtime.InvokeGlobal("test_initial_wire", type, "resource_value");
        Check(runtime.GetGlobal("initial_result").Value is DmResource, "Inherited immutable resource default changed.");
        runtime.InvokeGlobal("test_initial_wire", DmValue.Null, "scalar");
        Check(runtime.GetGlobal("initial_result").IsNull, "Null owner must return null for a string field.");
    }));
    tests.Add(($"initial never executes initializers or validates unrelated default values: {artifact}", () => {
        var json = InitialJson();
        var id = TypeId(json, "/datum/initial_effect");
        var variables = json["Types"]![id]!["Variables"]!;
        variables["unrelated"] = new JsonObject { ["type"] = 3, ["values"] = new JsonArray() };
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var type = new DmValue(new DmTypeReference(id, "/datum/initial_effect"));
        runtime.InvokeGlobal("test_initial", type);
        Check(runtime.GetGlobal("initial_result").Number == 11, "Targeted query converted unrelated mutable defaults.");
        runtime.InvokeGlobal("test_initial_wire", type, "effect");
        Check(runtime.GetGlobal("initial_result").IsNull && runtime.Logs.Count == 0 && runtime.PendingThreads == 0,
            "Initial executed an effectful initializer or fabricated its eventual value.");
        Throws(() => runtime.InvokeGlobal("test_initial_wire", type, "unrelated"), "Compiler value kind 3");
        Check(runtime.Logs.Count == 0, "Rejected mutable default produced effects.");
    }));
    tests.Add(($"initial static guards respect instance precedence and root-global noninheritance: {artifact}", () => {
        var json = InitialJson();
        var id = TypeId(json, "/datum/initial_parent/child");
        var globals = json["Globals"]!["Names"]!.AsArray();
        var index = Array.FindIndex(globals.Select(n => n!.GetValue<string>()).ToArray(), n => n == "initial_result");
        json["Types"]![0]!["GlobalVariables"] ??= new JsonObject();
        json["Types"]![0]!["GlobalVariables"]!["root_only"] = index;
        json["Types"]![id]!["GlobalVariables"] ??= new JsonObject();
        json["Types"]![id]!["GlobalVariables"]!["scalar"] = index;
        json["Types"]![id]!["Variables"]!.AsObject().Remove("scalar");
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var type = new DmValue(new DmTypeReference(id, "/datum/initial_parent/child"));
        Throws(() => runtime.InvokeGlobal("test_initial_wire", type, "shared"), "static/global field");
        runtime.InvokeGlobal("test_initial", type);
        Check(runtime.GetGlobal("initial_result").Number == 4, "Derived static mapping incorrectly shadowed inherited instance Variables.");
        runtime.InvokeGlobal("test_initial_wire", type, "root_only");
        Check(runtime.GetGlobal("initial_result").IsNull, "Root global was incorrectly inherited.");
        Throws(() => runtime.InvokeGlobal("test_initial_wire", new DmValue(new DmTypeReference(0, "/")), "root_only"), "static/global field");
    }));
    tests.Add(($"initial rejects native/meta/numeric-index and invalid owner/key forms: {artifact}", () => {
        var json = InitialJson();
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var type = new DmValue(new DmTypeReference(TypeId(json, "/datum/initial_parent"), "/datum/initial_parent"));
        foreach (var name in new[] { "type", "parent_type", "vars", "tag" })
            Throws(() => runtime.InvokeGlobal("test_initial_wire", type, name), "native/metadata");
        foreach (var (path, field) in new[] { ("/atom", "name"), ("/atom", "text"), ("/atom", "density"), ("/world", "maxx"), ("/list", "len"), ("/dm_filter", "x"), ("/pixloc", "x") })
            Throws(() => runtime.InvokeGlobal("test_initial_wire", new DmValue(new DmTypeReference(TypeId(json, path), path)), field), "native/metadata");
        runtime.InvokeGlobal("test_initial", new DmValue(new DmTypeReference(TypeId(json, "/filter"), "/filter")));
        Check(runtime.GetGlobal("initial_result").Number == 13, "User /filter path was misclassified as native /dm_filter.");
        Throws(() => runtime.InvokeGlobal("test_initial_wire", type, 1), "numeric indexing");
        Throws(() => runtime.InvokeGlobal("test_initial_wire", DmValue.Null, DmValue.Null), "string field name");
        foreach (var owner in new DmValue[] { 1, "text", new(new DmList()), new(new DmAssocList()) })
            Throws(() => runtime.InvokeGlobal("test_initial_wire", owner, "scalar"), "type or object owner");
        Throws(() => runtime.InvokeGlobal("test_initial_wire", new DmValue(new DmTypeReference(-1, "/bad")), "scalar"), "invalid type identity");
        Throws(() => runtime.InvokeGlobal("test_initial_wire", new DmValue(new DmObject(int.MaxValue, "/bad")), "scalar"), "invalid type identity");
    }));
    foreach (var kind in new[] { 2, 3, 6 })
        tests.Add(($"initial rejects requested unsupported encoded default {kind}: {artifact}", () => {
            var json = InitialJson();
            var id = TypeId(json, "/datum/initial_effect");
            var raw = new JsonObject { ["type"] = kind };
            if (kind == 2) raw["value"] = 0; else raw["values"] = new JsonArray();
            json["Types"]![id]!["Variables"]!["unsupported"] = raw;
            var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
            Throws(() => runtime.InvokeGlobal("test_initial_wire", new DmValue(new DmTypeReference(id, "/datum/initial_effect")), "unsupported"), $"Compiler value kind {kind}");
        }));
    tests.Add(($"initial ancestry work is bounded without partially publishing a result: {artifact}", () => {
        var json = InitialJson();
        var types = json["Types"]!.AsArray();
        var parent = TypeId(json, "/datum");
        for (var depth = 0; depth < 50; depth++) {
            var id = types.Count;
            types.Add(new JsonObject { ["Path"] = $"/datum/initial_deep_{depth}", ["Parent"] = parent, ["Variables"] = new JsonObject { ["scalar"] = 1 } });
            parent = id;
        }
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())) { InstructionBudget = 32 }; runtime.Boot();
        Throws(() => runtime.InvokeGlobal("test_initial", new DmValue(new DmTypeReference(parent, "/datum/initial_deep_49"))),
            "Instruction budget exhausted during initial ancestry lookup");
        Check(runtime.GetGlobal("initial_result").IsNull, "Failed lookup published an incomplete value.");
    }));
    tests.Add(($"static metadata remains optional and invalid mappings fail during load: {artifact}", () => {
        var json = InitialJson();
        foreach (var type in json["Types"]!.AsArray()) type!.AsObject().Remove("GlobalVariables");
        _ = CompiledProject.Parse(json.ToJsonString());
        var id = TypeId(json, "/datum/initial_parent");
        foreach (var (name, index) in new[] { ("bad", -1), ("bad", json["Globals"]!["GlobalCount"]!.GetValue<int>()), ("", 0) }) {
            json["Types"]![id]!["GlobalVariables"] = new JsonObject { [name] = index };
            Throws(() => CompiledProject.Parse(json.ToJsonString()), "invalid static/global variable mapping");
        }
    }));
}

var movableInitPath = args.Length > 7 ? args[7] : "build/movable-init.json";
var movableInitOptimizedPath = args.Length > 8 ? args[8] : "build/movable-init-optimized.json";
foreach (var artifact in new[] { movableInitPath, movableInitOptimizedPath }) {
    tests.Add(($"movable literal initialization is deferred and fresh: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact));
        var objects = runtime.MapCells.SelectMany(c => c.Entities).ToArray();
        Check(objects.Length == 4 && objects.All(o => o.Variables["items"].IsNull), "Movable initializer ran during map loading.");
        Check(runtime.Logs.Count == 0 && runtime.PendingThreads == 0, "Load produced callbacks or scheduled work.");
        // Model an earlier callback changing a published eager identity: its initializer
        // must still execute at its own phase and replace this placeholder.
        objects[0].Variables["items"] = 123;
        runtime.Boot();
        Check(runtime.Logs.Select(l => l.Text).SequenceEqual(new[] { "area", "turf", "turf", "obj=1", "obj=1", "obj=9", "nulled", "world" }),
            "Initializer/New ordering or deferred scheduling changed.");
        Check(objects.All(o => o.Variables["new_count"].Number == 1 && o.Variables["loc_seen"].Number == 1), "New count or location argument changed.");
        Check(objects.Take(3).All(o => o.Variables["usr_seen"].Number == 1), "Map root usr must be null.");
        var first = (DmList)objects[0].Variables["items"].Value!;
        var second = (DmList)objects[1].Variables["items"].Value!;
        Check(!ReferenceEquals(first, second) && !ReferenceEquals(first.Values[1].Value, second.Values[1].Value), "Inherited lists share instance state.");
        ((DmList)first.Values[1].Value!).Values[0] = 99;
        Check(((DmList)second.Values[1].Value!).Values[0].Number == 2, "Nested list mutation leaked.");
        Check(objects[3].Variables["items"].IsNull, "Constant child override lost to inherited initialization.");
        Check(objects.Select(o => o.Variables["empty"].Value).Distinct().Count() == 4, "Empty lists share state.");
        var keyed = (DmList)objects[0].Variables["keyed"].Value!;
        Check(keyed.Values.Count == 2 && ((DmList)keyed.Get("a").Value!).Values[0].Number == 4, "Named associations or duplicate keys changed.");
        Check(keyed.Values[1].Value is DmList && keyed.Get(keyed.Values[1]).Value is DmTypeReference, "List key identity/type value lost.");
        var matrix = (DmList)objects[0].Variables["sized"].Value!;
        Check(matrix.Values.Count == 2 && !ReferenceEquals(matrix.Values[0].Value, matrix.Values[1].Value), "Dimension rows alias.");
        Check(matrix.Values.All(v => v.Value is DmList row && row.Values.Count == 3 && row.Values.All(x => x.IsNull)), "Dimension shape/null leaves changed.");
        runtime.AdvanceBy(1);
        Check(runtime.Logs.TakeLast(3).Select(l => l.Text).SequenceEqual(new[] { "awake=1", "awake=1", "awake=9" }), "Movable sleep continuation changed.");
    }));
    tests.Add(($"ordinary associative constructor preserves ordered entries: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        runtime.InvokeGlobal("literal_lists");
        var list = (DmList)runtime.GetGlobal("result").Value!;
        Check(list.Values.Select(v => v.ToString()).SequenceEqual(new[] { "replaced", "plain", "null-value", "grown", "plain" }), "Mixed key/value entry order changed.");
        Check(list.Get("a").Number == 2, "Numeric replacement incorrectly removed old association.");
        runtime.InvokeGlobal("bare_null_key");
        var namedNull = (DmList)runtime.GetGlobal("result").Value!;
        Check(namedNull.Values.Single().ToString() == "null" && namedNull.Get("null").ToString() == "named-null", "Compiler bare null key must remain a named string key.");
        runtime.InvokeGlobal("fractional_list_keys");
        list = (DmList)runtime.GetGlobal("result").Value!;
        Check(list.Get(1.9f).ToString() == "changed" && list.Get(2.9f).ToString() == "second", "Numeric constructor/read indices must truncate.");
        list.Set(1.9f, "write");
        Check(list.Get(1).ToString() == "write", "Numeric list writes must truncate.");
        foreach (var proc in new[] { "assoc_zero", "assoc_gap" }) Throws(() => runtime.InvokeGlobal(proc), "list index out of bounds");
        foreach (var number in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue })
            Throws(() => runtime.InvokeGlobal("assoc_number", new DmValue(number)), "out-of-range list size/index");
    }));
    tests.Add(($"dimensioned constructors coerce values and bound aggregate work: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        runtime.InvokeGlobal("dimension_sizes", 2.9f, 3.9f);
        var list = (DmList)runtime.GetGlobal("result").Value!;
        Check(list.Values.Count == 2 && list.Values.All(v => ((DmList)v.Value!).Values.Count == 3), "Dimensions do not truncate.");
        runtime.InvokeGlobal("dimension_sizes", 2, -3);
        list = (DmList)runtime.GetGlobal("result").Value!;
        Check(list.Values.Count == 2 && list.Values.All(v => ((DmList)v.Value!).Values.Count == 0), "Multidimension negative size must become zero.");
        runtime.InvokeGlobal("dimension_sizes", "bad", 3);
        Check(((DmList)runtime.GetGlobal("result").Value!).Values.Count == 0, "Nonnumeric dimension must become zero.");
        runtime.InvokeGlobal("single_dimension", "bad");
        Check(((DmList)runtime.GetGlobal("result").Value!).Values.Count == 0, "Nonnumeric single dimension must be empty.");
        Throws(() => runtime.InvokeGlobal("single_dimension", -1), "Negative single-dimension");
        foreach (var number in new[] { float.NaN, float.PositiveInfinity, float.MaxValue })
            Throws(() => runtime.InvokeGlobal("single_dimension", new DmValue(number)), "out-of-range list size/index");
        Throws(() => runtime.InvokeGlobal("dimension_sizes", 1000, 1000), "allocation budget");
        Throws(() => runtime.InvokeGlobal("aggregate_lists"), "allocation budget");
        runtime.InvokeGlobal("single_dimension", 3);
        Check(((DmList)runtime.GetGlobal("result").Value!).Values.Count == 3, "Allocation budget did not reset for next invocation.");
    }));
    tests.Add(($"Boot retains list budget across deferred continuations: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var code = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "boot_allocation_budget")!["Bytecode"]!.DeepClone();
        var worldId = TypeId(json, "/world");
        var worldNew = json["Procs"]!.AsArray().Last(p => p!["Name"]!.GetValue<string>() == "New" && p["OwningTypeId"]!.GetValue<int>() == worldId)!;
        worldNew["Bytecode"] = code;
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
        Throws(runtime.Boot, "allocation budget");
        runtime.InvokeGlobal("single_dimension", 3);
        Check(((DmList)runtime.GetGlobal("result").Value!).Values.Count == 3, "Next public execution boundary failed to reset budget.");
    }));
    foreach (var unsafeType in new[] { "/obj/unsafe_helper", "/obj/unsafe_sleep", "/obj/unsafe_read", "/obj/unsafe_native", "/obj/unsafe_new", "/obj/local/unsafe_child" })
        tests.Add(($"mapped initializer preflight rejects {unsafeType}: {artifact}", () => {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            // Last instance is unsafe: validation must finish before any Boot effects.
            json["Maps"]![0]!["CellDefinitions"]!["b"]!["Objects"]![1]!["Type"] = TypeId(json, unsafeType);
            try {
                _ = new DmRuntime(CompiledProject.Parse(json.ToJsonString()));
                throw new Exception("Unsupported movable initializer accepted.");
            } catch (CompatibilityException ex) {
                Check(ex.Message.Contains("Movable initializer") && ex.Message.Contains("movable-init.dm:"), "Missing source-located preflight diagnostic: " + ex.Message);
            }
        }));
}

var alistPath = args.Length > 9 ? args[9] : "build/alist.json";
var alistOptimizedPath = args.Length > 10 ? args[10] : "build/alist-optimized.json";
foreach (var artifact in new[] { alistPath, alistOptimizedPath }) {
    DmRuntime AlistRuntime() { var runtime = new DmRuntime(CompiledProject.Load(artifact)) { InstructionBudget = 2_000_000 }; runtime.Boot(); return runtime; }
    tests.Add(($"strict associative literals retain exact keys: {artifact}", () => {
        var runtime = AlistRuntime(); runtime.InvokeGlobal("key_literals");
        var list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 6 && list.Get(0).ToString() == "updated" && list.Get(-1).ToString() == "negative", "Numeric keys were positional or duplicates changed length.");
        Check(list.Get(2.9f).ToString() == "fraction" && list.Get(2).IsNull, "Fractional key was truncated.");
        Check(list.Get(DmValue.Null).ToString() == "true-null" && list.Get("null").ToString() == "named-null", "True null and parser string-null keys collided.");
        Check(list.ContainsKey("empty") && list.Get("empty").IsNull && !list.ContainsKey("absent"), "Null-valued entry or absent key changed.");
        runtime.InvokeGlobal("lengths", new DmValue(list));
        Check(((DmList)runtime.GetGlobal("result").Value!).Values.All(v => v.Number == 6), "length/len dispatch lost strict list entries.");
        runtime.InvokeGlobal("exact_index", new DmValue(list), 2.9f);
        Check(runtime.GetGlobal("result").ToString() == "fraction", "Strict index read used ordinary positional coercion.");
        runtime.InvokeGlobal("index_update", new DmValue(list), 0, 7);
        runtime.InvokeGlobal("separate_index_update", new DmValue(list), 0);
        Throws(() => runtime.InvokeGlobal("compound_index", new DmValue(list), 0), "Compound indexed/field assignment");
        Check(runtime.GetGlobal("result").Number == 8 && list.Count == 6, "Reference-index update/read or duplicate assignment changed.");
    }));
    tests.Add(($"strict numeric keys preserve infinities NaN and signed zero: {artifact}", () => {
        var runtime = AlistRuntime();
        runtime.InvokeGlobal("numeric_keys", new DmValue(float.PositiveInfinity), new DmValue(float.NegativeInfinity), new DmValue(float.NaN));
        var list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 3 && list.Get(new(float.PositiveInfinity)).Number == 1 && list.Get(new(float.NegativeInfinity)).Number == 2 &&
            list.Get(new(float.NaN)).Number == 3, "Nonfinite keys were rejected or conflated.");
        runtime.InvokeGlobal("numeric_keys", 0f, -0f, new DmValue(float.NaN));
        list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 2 && list.Get(0).Number == 2, "Signed zero equality differs from pinned value equality.");
        runtime.InvokeGlobal("index_update", new DmValue(list), new DmValue(float.NaN), 9);
        Check(list.Count == 2 && list.Get(new(float.NaN)).Number == 9, "Repeated NaN key did not replace its value.");
    }));
    tests.Add(($"strict list object and collection keys use reference identity: {artifact}", () => {
        var runtime = AlistRuntime(); runtime.InvokeGlobal("identity_keys");
        var list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 4 && list.Get(runtime.GetGlobal("key_one")).Number == 1 && list.Get(runtime.GetGlobal("key_two")).Number == 2 &&
            list.Get(runtime.GetGlobal("strict_one")).Number == 3 && list.Get(runtime.GetGlobal("strict_two")).Number == 4, "Equal-content collection keys collided.");
        runtime.InvokeGlobal("object_keys");
        list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 2 && list.Get(runtime.GetGlobal("key_one")).Number == 1 && list.Get(runtime.GetGlobal("key_two")).Number == 2, "Datum keys lost identity.");
        runtime.InvokeGlobal("value_properties");
        Check(((DmList)runtime.GetGlobal("result").Value!).Values.Select(v => v.Number).SequenceEqual(new float[] { 1, 0, 1, 0, 0 }), "Strict lists lost identity equality, truthiness or empty length.");
    }));
    tests.Add(($"strict inherited mutable defaults run per instance at Boot: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact));
        var objects = runtime.MapCells.SelectMany(c => c.Entities).ToArray();
        Check(objects.All(o => o.Variables["items"].IsNull), "Strict initializer ran before Boot.");
        runtime.Boot();
        var first = (DmAssocList)objects[0].Variables["items"].Value!;
        var second = (DmAssocList)objects[1].Variables["items"].Value!;
        Check(!ReferenceEquals(first, second) && !ReferenceEquals(first.Get(0).Value, second.Get(0).Value), "Strict/nested list defaults are shared.");
        ((DmList)first.Get(0).Value!).Values[0] = 77;
        Check(((DmList)second.Get(0).Value!).Values[0].Number == 1, "Nested mutation leaked.");
        Check(((DmList)((DmAssocList)objects[2].Variables["items"].Value!).Get(0).Value!).Values[0].Number == 9 &&
            objects[3].Variables["items"].IsNull, "Child initializer override order changed.");
        Check(objects.All(o => o.Variables["new_count"].Number == 1) && objects.Select(o => o.Variables["empty"].Value).Distinct().Count() == 4, "New counts or fresh empty defaults changed.");
    }));
    foreach (var (proc, error) in new[] {
        ("type_guard", "alist.type"), ("islist_guard", "islist"), ("istype_guard", "not implemented"),
        ("string_guard", "stringification"), ("output_guard", "stringification"),
        ("operator_guard", "Expected a DM number"), ("enumeration_guard", "not implemented"),
        ("native_guard", "non-object receiver"), ("new_guard", "native/atom"),
        ("len_write_guard", "Mutation of alist.len"), ("type_write_guard", "Mutation of alist.type")
    }) tests.Add(($"strict unsupported operation stays explicit {proc}: {artifact}", () => {
        var runtime = AlistRuntime();
        Throws(() => runtime.InvokeGlobal(proc), error);
    }));
    tests.Add(($"strict list insertion budget charges new keys only: {artifact}", () => {
        var runtime = AlistRuntime();
        Throws(() => runtime.InvokeGlobal("growth_budget", 1), "allocation budget");
        var list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 49_999 && list.Get(49_999).Number == 49_999 && !list.ContainsKey(50_000), "Rejected insertion mutated the list or escaped the budget.");
        runtime.InvokeGlobal("overwrite_budget", 1);
        list = (DmAssocList)runtime.GetGlobal("result").Value!;
        Check(list.Count == 1 && list.Get(0).Number == 60_000, "Overwriting an existing key consumed new-key budget.");
    }));
    foreach (var count in new[] { -1, 1, int.MaxValue })
        tests.Add(($"strict malformed pair count is rejected {count}: {artifact}", () => {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            var proc = json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "probe")!;
            proc["Bytecode"] = Convert.ToBase64String(new[] { (byte)DreamProcOpcode.CreateStrictAssociativeList }.Concat(BitConverter.GetBytes(count)).Append((byte)DreamProcOpcode.Return).ToArray());
            var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
            Throws(() => runtime.InvokeGlobal("probe"), "strict associative list pair count");
        }));
    tests.Add(($"strict constructor allocation is bounded before materialization: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) {
            writer.Write((byte)DreamProcOpcode.PushNFloats); writer.Write(100_000);
            for (var i = 0; i < 100_000; i++) writer.Write(0f);
            writer.Write((byte)DreamProcOpcode.CreateStrictAssociativeList); writer.Write(50_000);
            writer.Write((byte)DreamProcOpcode.Return);
        }
        json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "probe")!["Bytecode"] = Convert.ToBase64String(bytes.ToArray());
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        Throws(() => runtime.InvokeGlobal("probe"), "allocation budget");
    }));
    tests.Add(($"strict reference-index read dispatch stays distinct: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var globalIndex = json["Globals"]!["Names"]!.AsArray().Select((name, index) => (name, index)).Single(p => p.name!.GetValue<string>() == "result").index;
        var code = new List<byte> {
            (byte)DreamProcOpcode.PushReferenceValue, 8, 0,
            (byte)DreamProcOpcode.PushReferenceValue, 8, 1,
            (byte)DreamProcOpcode.PushReferenceValue, 7,
            (byte)DreamProcOpcode.AssignNoPush, 10
        };
        code.AddRange(BitConverter.GetBytes(globalIndex));
        code.Add((byte)DreamProcOpcode.PushNull); code.Add((byte)DreamProcOpcode.Return);
        json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "probe")!["Bytecode"] = Convert.ToBase64String(code.ToArray());
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        var list = new DmAssocList(); list.Set(2.9f, 91);
        runtime.InvokeGlobal("probe", new DmValue(list), 2.9f);
        Check(runtime.GetGlobal("result").Number == 91, "Reference kind7 used ordinary list indexing.");
    }));
    tests.Add(($"strict truncated pair operand stays rejected by loader: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        json["Procs"]!.AsArray().Single(p => p!["Name"]!.GetValue<string>() == "probe")!["Bytecode"] =
            Convert.ToBase64String(new byte[] { (byte)DreamProcOpcode.CreateStrictAssociativeList, 1 });
        Throws(() => CompiledProject.Parse(json.ToJsonString()), "truncated");
    }));
    foreach (var type in new[] { "/obj/unsafe_alist_helper", "/obj/unsafe_alist_read", "/obj/unsafe_alist_native" })
        tests.Add(($"strict initializer does not widen effect permission {type}: {artifact}", () => {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            json["Maps"]![0]!["CellDefinitions"]!["b"]!["Objects"]![1]!["Type"] = TypeId(json, type);
            Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Movable initializer");
        }));
    foreach (var referenceKind in new byte[] { 10, 13 })
        tests.Add(($"optimized null assignment cannot widen initializer writes {referenceKind}: {artifact}", () => {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            var id = referenceKind == 10
                ? json["Globals"]!["Names"]!.AsArray().Select((name, index) => (name, index)).Single(p => p.name!.GetValue<string>() == "result").index
                : json["Strings"]!.AsArray().Select((name, index) => (name, index)).Single(p => p.name!.GetValue<string>() == "type").index;
            var init = json["Types"]![TypeId(json, "/obj/holder")]!["InitProc"]!.GetValue<int>();
            var code = new List<byte> { (byte)DreamProcOpcode.NullRef, referenceKind };
            code.AddRange(BitConverter.GetBytes(id)); code.Add((byte)DreamProcOpcode.PushNull); code.Add((byte)DreamProcOpcode.Return);
            json["Procs"]![init]!["Bytecode"] = Convert.ToBase64String(code.ToArray());
            Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())),
                referenceKind == 10 ? "own user variables" : "native/metadata assignment");
        }));
    tests.Add(($"strict tagged compiler constant stays out of shared defaults: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        json["Types"]![TypeId(json, "/obj/holder")]!["Variables"]!["items"] = new JsonObject { ["type"] = 6, ["values"] = new JsonArray() };
        Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "Compiler value kind 6");
    }));
}

var entityOverridePath = args.Length > 11 ? args[11] : "build/entity-overrides.json";
var entityOverrideOptimizedPath = args.Length > 12 ? args[12] : "build/entity-overrides-optimized.json";
foreach (var artifact in new[] { entityOverridePath, entityOverrideOptimizedPath }) {
    tests.Add(($"entity raw overrides precede inherited initialization and New: {artifact}", () => {
        var project = CompiledProject.Load(artifact);
        var runtime = new DmRuntime(project);
        var objects = runtime.MapCells.SelectMany(c => c.Entities).ToArray();
        Check(objects[0].Variables["items"].Number == 42 && objects[1].Variables["strict"].Number == 44 &&
            objects[2].Variables["items"].IsNull, "Raw overrides were lost or initializer ran early.");
        runtime.Boot();
        Check(objects.Take(4).All(o => o.Variables["loc_seen"].Number == 1), "Native loc/New argument order changed.");
        Check(objects.Take(4).Select(o => o.Variables["item_seen"].Number).SequenceEqual(new float[] { 2, 2, 2, 9 }), "Inherited/child initializer assignments must win over DMM overrides.");
        Check(objects.Take(4).All(o => o.Variables["strict_seen"].Number == 4), "Strict initializer did not replace raw overrides.");
        Check(objects.Take(3).Select(o => o.Variables["scalar_seen"].Number).SequenceEqual(new float[] { 11, 12, 1 }), "Scalar overrides leaked between cells or into type defaults.");
        Check(objects.Take(3).Select(o => o.Variables["name_seen"].ToString()).SequenceEqual(new[] { "A", "B", "inherited name" }), "Name override/inheritance order changed.");
        Check(objects[0].Variables["state_seen"].ToString() == "missing-state" && objects[1].Variables["state_seen"].Value is string empty && empty.Length == 0,
            "State strings require no loaded icon and empty must remain distinct from null.");
        var first = (DmList)objects[0].Variables["items"].Value!;
        var second = (DmList)objects[1].Variables["items"].Value!;
        Check(!ReferenceEquals(first, second) && !ReferenceEquals(first.Values[1].Value, second.Values[1].Value), "Per-instance nested list defaults are shared.");
        ((DmList)first.Values[1].Value!).Values[0] = 99;
        Check(((DmList)second.Values[1].Value!).Values[0].Number == 3, "Nested mutation crossed instance boundaries.");
        var a = (DmAssocList)objects[0].Variables["strict"].Value!;
        var b = (DmAssocList)objects[1].Variables["strict"].Value!;
        Check(!ReferenceEquals(a, b) && !ReferenceEquals(a.Get(0).Value, b.Get(0).Value), "Strict initializer lists are shared.");
        var fresh = new DmRuntime(project);
        Check(fresh.MapCells[2].Entities[0].Variables["scalar"].Number == 1 && fresh.MapCells[0].Entities[0].Variables["items"].Number == 42,
            "Runtime mutated shared project/cache defaults.");
        Check(objects[4].Variables["name_seen"].ToString() == "null name", "Null type name fallback must use the final type name.");
        Check(objects[5].Variables["name_seen"].ToString() == "leaf thing" && objects[6].Variables["name_seen"].ToString() == "reset name",
            "Inherited raw-null or explicit child-null names must use the child leaf, not a parent's normalized name.");
    }));
    tests.Add(($"movable appearance reads normalize supported raw values independently: {artifact}", () => {
        var original = JsonNode.Parse(File.ReadAllText(artifact))!;
        var values = new JsonNode?[] { null, JsonValue.Create(17), JsonValue.Create(""), JsonValue.Create("custom"),
            new JsonObject { ["type"] = 1, ["value"] = TypeId(original, "/obj/base") },
            new JsonObject { ["type"] = 0, ["resourcePath"] = "not-loaded.dmi" },
            new JsonObject { ["type"] = 4 }, new JsonObject { ["type"] = 5 } };
        foreach (var raw in values) {
            var json = original.DeepClone();
            var overrides = json["Maps"]![0]!["CellDefinitions"]!["a"]!["Objects"]![0]!["VarOverrides"]!;
            overrides["name"] = raw?.DeepClone(); overrides["icon_state"] = raw?.DeepClone(); overrides["scalar"] = raw?.DeepClone();
            var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
            var obj = runtime.MapCells[0].Entities[0];
            var expectedString = raw is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            Check(obj.Variables["name_seen"].ToString() == (expectedString ?? ""), "Nonstring name must normalize to empty string.");
            Check(expectedString is null ? obj.Variables["state_seen"].IsNull : obj.Variables["state_seen"].Value is string state && state == expectedString,
                "Nonstring icon_state must normalize to null, preserving empty strings.");
            Check(obj.Variables["scalar_seen"].Equals(obj.Variables["scalar"]), "Declared user scalar was normalized as native appearance.");
            Check(obj.Variables["name"].Equals(obj.Variables["scalar"]) && obj.Variables["icon_state"].Equals(obj.Variables["scalar"]),
                "Read normalization destroyed raw map override values.");
            Check(runtime.MapCells[2].Entities[0].Variables["name_seen"].ToString() == "inherited name", "Override polluted same-type default.");
        }
        var nullName = original.DeepClone();
        nullName["Maps"]![0]!["CellDefinitions"]!["e"]!["Objects"]![0]!["VarOverrides"] = new JsonObject { ["name"] = null };
        var explicitNull = new DmRuntime(CompiledProject.Parse(nullName.ToJsonString())); explicitNull.Boot();
        Check(explicitNull.MapCells[4].Entities[0].Variables["name_seen"].ToString() == "", "Explicit null override must not regenerate a type name.");
    }));
    foreach (var (field, reason) in new[] {
        ("unknown_field", "unknown or non-instance"), ("only_global", "unknown or non-instance"),
        ("fixed", "const field"), ("type", "metadata/tag"), ("parent_type", "metadata/tag"), ("vars", "metadata/tag"), ("tag", "metadata/tag"),
        ("loc", "native field"), ("x", "native field"), ("dir", "native field"), ("icon", "native field"), ("layer", "native field"),
        ("density", "native field"), ("screen_loc", "native field")
    }) tests.Add(($"entity override rejects {field}: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        json["Maps"]![0]!["CellDefinitions"]!["a"]!["Objects"]![0]!["VarOverrides"] = new JsonObject { [field] = 1 };
        try { _ = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); throw new Exception("Unsupported override accepted."); }
        catch (CompatibilityException ex) {
            Check(ex.Message.Contains(reason) && ex.Message.Contains("/obj/base/child") && ex.Message.Contains(field) && ex.Message.Contains("Number"),
                "Override diagnostic lost type/field/value category: " + ex.Message);
        }
    }));
    foreach (var kind in new[] { 2, 3, 6 })
        tests.Add(($"entity override rejects unsupported compiler value {kind}: {artifact}", () => {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            var raw = new JsonObject { ["type"] = kind };
            if (kind == 2) raw["value"] = 0; else raw["values"] = new JsonArray();
            json["Maps"]![0]!["CellDefinitions"]!["a"]!["Objects"]![0]!["VarOverrides"] = new JsonObject { ["scalar"] = raw };
            Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), $"compiler tag {kind}");
        }));
    foreach (var (proc, message) in new[] {
        ("write_name", "Mutation of map atom.name"), ("write_state", "Mutation of map atom.icon_state"),
        ("output_object", "Map atom stringification"), ("interpolate_object", "Object/article string interpolation"),
        ("read_other_native", "Map atom.dir"), ("read_turf_state", "Map atom.icon_state")
    }) tests.Add(($"entity appearance remains read-only: {proc}/{artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        Throws(() => runtime.InvokeGlobal(proc, new DmValue(runtime.MapCells[0].Entities[0])), message);
    }));
    foreach (var type in new[] { "/obj/bad_init", "/obj/bad_state_init" })
        tests.Add(($"entity overrides do not permit native initializer writes: {type}/{artifact}", () => {
            var json = JsonNode.Parse(File.ReadAllText(artifact))!;
            var entity = json["Maps"]![0]!["CellDefinitions"]!["a"]!["Objects"]![0]!;
            entity["Type"] = TypeId(json, type); entity["VarOverrides"] = new JsonObject { ["name"] = "mapped", ["icon_state"] = "mapped" };
            Throws(() => new DmRuntime(CompiledProject.Parse(json.ToJsonString())), "native/metadata assignment");
        }));
    tests.Add(($"const metadata remains optional but rejects malformed names: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        json["Types"]![TypeId(json, "/obj/base")]!.AsObject().Remove("ConstVariables");
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        json["Types"]![TypeId(json, "/obj/base")]!["ConstVariables"] = new JsonArray((JsonNode?)null);
        Throws(() => CompiledProject.Parse(json.ToJsonString()), "const variable name");
    }));
}


foreach (var artifact in new[] { entityOverridePath, entityOverrideOptimizedPath })
    tests.Add(($"initial ignores mapped prototype overrides and current dictionary mutation: {artifact}", () => {
        var runtime = new DmRuntime(CompiledProject.Load(artifact)); runtime.Boot();
        var first = runtime.MapCells[0].Entities[0];
        Check(first.Variables["scalar"].Number == 11, "Fixture lost its raw map override.");
        runtime.InvokeGlobal("test_entity_initial", new DmValue(first));
        Check(runtime.GetGlobal("entity_initial_result").Number == 1, "Initial read cloned map definition instead of original type.");
        first.Variables.Clear();
        runtime.InvokeGlobal("test_entity_initial", new DmValue(first));
        Check(runtime.GetGlobal("entity_initial_result").Number == 1, "Initial consulted the current public dictionary.");
    }));


foreach (var artifact in new[] { semanticsPath, optimizedPath }) {
    tests.Add(($"file snapshot exact presence, paths and defensive ownership: {artifact}", () => {
        var names = new HashSet<string> { "config/present.txt", "Case.txt", "dir/child" };
        var blocked = new HashSet<string> { "link", "submodule" };
        var inventory = new DmFileInventory(names, blocked);
        names.Clear(); names.Add("ambient.txt"); blocked.Clear();
        var runtime = new DmRuntime(CompiledProject.Load(artifact), inventory); runtime.Boot();
        foreach (var (name, expected) in new[] { ("config/present.txt", 1f), ("Case.txt", 1f),
            ("case.txt", 0f), ("ambient.txt", 0f), ("missing.txt", 0f), ("dir", 0f), ("dir/", 0f), ("", 0f) }) {
            runtime.InvokeGlobal("test_file_snapshot", new DmValue(name));
            Check(runtime.GetGlobal("file_snapshot_result").Number == expected, "Snapshot presence changed: " + name);
        }
        runtime.InvokeGlobal("test_file_snapshot", new DmValue(new DmResource("config/present.txt")));
        Check(runtime.GetGlobal("file_snapshot_result").Number == 1, "Resource did not query its logical path.");
        runtime.InvokeGlobal("test_file_snapshot", new DmValue(new DmResource("missing.txt")));
        Check(runtime.GetGlobal("file_snapshot_result").Number == 0, "Resource object was mistaken for an existing file.");
        foreach (var name in new[] { "link", "link/child", "link/", "submodule/file" })
            Throws(() => runtime.InvokeGlobal("test_file_snapshot", new DmValue(name)), "symlink or submodule");
        foreach (var name in new[] { "/absolute", "C:/file", @"C:\file", @"\\host\share", "../sibling",
            "a/../b", "./file", "a//b", "a/./b", ".git/config", "a/.GIT/config", "a\0b", "a\nb", "*.txt", @"a\b" })
            Throws(() => runtime.InvokeGlobal("test_file_snapshot", new DmValue(name)), "file snapshot path");
        Throws(() => runtime.InvokeGlobal("test_file_snapshot", new DmValue(new DmResource("../vendor/file"))), "file snapshot path");
        Throws(() => runtime.InvokeGlobal("test_file_snapshot_read", new DmValue("config/present.txt")), "file2text");
    }));
    tests.Add(($"file snapshot default denial, empty capability and invalid arguments: {artifact}", () => {
        var project = CompiledProject.Load(artifact);
        var runtime = new DmRuntime(project); runtime.Boot();
        foreach (var value in new[] { DmValue.Null, new DmValue(1f), new DmValue(new DmList()),
            new DmValue(new DmAssocList()), new DmValue(runtime.World), new DmValue(new DmTypeReference(0, "/")) }) {
            runtime.InvokeGlobal("test_file_snapshot", value);
            Check(runtime.GetGlobal("file_snapshot_result").IsNull, "Non-path argument must return null without capability.");
        }
        foreach (var value in new[] { new DmValue(""), new DmValue(new DmResource(null!)) }) {
            runtime.InvokeGlobal("test_file_snapshot", value);
            Check(runtime.GetGlobal("file_snapshot_result").Number == 0, "Empty path must return false.");
        }
        Throws(() => runtime.InvokeGlobal("test_file_snapshot", new DmValue("file")), "explicitly configured");
        var empty = new DmRuntime(project, new DmFileInventory([])); empty.Boot();
        empty.InvokeGlobal("test_file_snapshot", new DmValue("file"));
        Check(empty.GetGlobal("file_snapshot_result").Number == 0, "Configured empty must differ from missing capability.");
        var bounded = new DmRuntime(project, new DmFileInventory([])) { InstructionBudget = 100 }; bounded.Boot();
        Throws(() => bounded.InvokeGlobal("test_file_snapshot", new DmValue(new string('x', 20))), "path work budget");
        Throws(() => empty.InvokeGlobal("test_file_snapshot", new DmValue(new string('x', 4097))), "path length");
    }));
    tests.Add(($"executable fexists body is not replaced by native interception: {artifact}", () => {
        var json = JsonNode.Parse(File.ReadAllText(artifact))!;
        var proc = json["Procs"]!.AsArray().First(p => p!["Name"]!.GetValue<string>() == "fexists")!;
        var bytes = new List<byte> { (byte)DreamProcOpcode.ReturnFloat };
        bytes.AddRange(BitConverter.GetBytes(7f)); proc["Bytecode"] = Convert.ToBase64String(bytes.ToArray());
        var runtime = new DmRuntime(CompiledProject.Parse(json.ToJsonString())); runtime.Boot();
        runtime.InvokeGlobal("test_file_snapshot", new DmValue("../rejected-by-native"));
        Check(runtime.GetGlobal("file_snapshot_result").Number == 7, "Executable native-name body intercepted.");
    }));
}
tests.Add(("file inventory ingestion rejects duplicates, unsafe entries and bounded growth", () => {
    Throws(() => new DmFileInventory(["x", "x"]), "Duplicate");
    var prefix = string.Concat(Enumerable.Repeat("a/", 500));
    Throws(() => new DmFileInventory(Enumerable.Range(0, 1000).Select(i => prefix + i), ["blocked"]), "prefix work");
    Throws(() => new DmFileInventory([""]), "Empty");
    Throws(() => new DmFileInventory(["dir/"]), "path component");
    Throws(() => new DmFileInventory(["x/file"], ["x"]), "overlaps");
    Throws(() => new DmFileInventory(Enumerable.Range(0, 100001).Select(i => "f" + i)), "count limit");
    Throws(() => new DmFileInventory(Enumerable.Range(0, 5000).Select(i => new string('x', 4000) + i)), "byte limit");
}));


tests.Add(("file snapshot never falls back to a real current-directory file", () => {
    var project = CompiledProject.Load(semanticsPath);
    var originalDirectory = Directory.GetCurrentDirectory();
    var fixtureDirectory = Path.Combine(Path.GetTempPath(), "dm-file-snapshot-" + Guid.NewGuid());
    Directory.CreateDirectory(fixtureDirectory);
    try {
        File.WriteAllText(Path.Combine(fixtureDirectory, "decoy.txt"), "isolated test fixture");
        Directory.SetCurrentDirectory(fixtureDirectory);
        var configured = new DmRuntime(project, new DmFileInventory([])); configured.Boot();
        configured.InvokeGlobal("test_file_snapshot", new DmValue("decoy.txt"));
        Check(configured.GetGlobal("file_snapshot_result").Number == 0, "Snapshot consulted ambient cwd.");
        var unconfigured = new DmRuntime(project); unconfigured.Boot();
        Throws(() => unconfigured.InvokeGlobal("test_file_snapshot", new DmValue("decoy.txt")), "explicitly configured");
    } finally {
        Directory.SetCurrentDirectory(originalDirectory);
        Directory.Delete(fixtureDirectory, recursive: true);
    }
}));

var failed = 0;
foreach (var (name, run) in tests) {
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} runtime tests passed.");
return failed == 0 ? 0 : 1;
