using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DmGodot.Runtime;

// Synthetic construction measurement, NOT compiler output or Civ13 acceptance.
// No Boot(), scheduler advancement, host, sockets, native services or save access.
try {
    if (args.Length != 4 || !int.TryParse(args[0], out var side) || side < 1 || side > 128 ||
        !int.TryParse(args[1], out var fields) || fields < 0 || fields > 128 ||
        !int.TryParse(args[2], out var repetition) || repetition < 1 || repetition > 3 ||
        args[3].Length != 40 || !args[3].All(Uri.IsHexDigit))
        throw new ArgumentException("Expected side 1..128, fields 0..128, repetition 1..3, engine commit SHA.");
    Warmup();
    var (project, hash, artifactBytes) = Prepare(side, fields);
    var report = Measure(project, side, fields);
    report["schema"] = 1;
    report["status"] = "completed";
    report["scope"] = "Synthetic map construction only; not compiler output, Civ13 attribution or gameplay acceptance.";
    report["engine_revision"] = args[3];
    report["compiler_revision"] = CompiledProject.CompilerRevision;
    report["bytecode_version"] = project.BytecodeVersion;
    report["artifact_sha256"] = hash;
    report["artifact_bytes"] = artifactBytes;
    report["side"] = side;
    report["extra_inherited_fields"] = fields;
    report["repetition"] = repetition;
    report["dotnet"] = RuntimeInformation.FrameworkDescription;
    report["os"] = RuntimeInformation.OSDescription;
    report["architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
    report["gc_server"] = GCSettings.IsServerGC;
    report["gc_latency_mode"] = GCSettings.LatencyMode.ToString();
    report["processor_count"] = Environment.ProcessorCount;
    report["configured_gc_heap_hard_limit"] = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit");
    report["gc_total_available_memory_bytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
} catch (Exception error) {
    Console.WriteLine(JsonSerializer.Serialize(new {
        schema = 1, status = "error", exception_type = error.GetType().FullName, message = error.Message
    }));
    return 1;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void Warmup() {
    var (project, _, _) = Prepare(2, 1);
    var runtime = new DmRuntime(project);
    GC.KeepAlive(runtime);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static (CompiledProject, string, int) Prepare(int side, int fields) {
    // Fixed ancestry, one shared area and one repeated turf prototype.
    // Scalars on /atom are copied by inherited variable initialization.
    var inherited = Enumerable.Range(0, fields).ToDictionary(i => $"field_{i:D3}", i => (object)i);
    object Type(string path, int? parent, object? variables = null) => new {
        Path = path, Parent = parent, Variables = variables
    };
    var artifact = new {
        Metadata = new { Version = CompiledProject.SupportedBytecodeVersion },
        Strings = Array.Empty<string>(), Procs = Array.Empty<object>(),
        Types = new[] {
            Type("/", null), Type("/datum", 0), Type("/atom", 1, inherited),
            Type("/turf", 2), Type("/area", 2),
            Type("/world", 1, new { area = new { type = 1, value = 4 }, turf = new { type = 1, value = 3 } })
        },
        Maps = new[] { new {
            MaxX = side, MaxY = side, MaxZ = 1,
            CellDefinitions = new Dictionary<string, object> {
                ["a"] = new { Turf = new { Type = 3 }, Area = new { Type = 4 }, Objects = Array.Empty<object>() }
            },
            Blocks = new[] { new {
                X = 1, Y = 1, Z = 1, Width = side, Height = side,
                Cells = Enumerable.Repeat("a", side * side).ToArray()
            }}
        }}
    };
    var json = JsonSerializer.SerializeToUtf8Bytes(artifact);
    var hash = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    return (CompiledProject.Parse(System.Text.Encoding.UTF8.GetString(json), "synthetic-map-benchmark.json"), hash, json.Length);
}

static void FullCollection() {
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static Dictionary<string, object?> Measure(CompiledProject project, int side, int fields) {
    var timer = new Stopwatch();
    FullCollection();
    var retainedBefore = GC.GetTotalMemory(false);
    var gen0 = GC.CollectionCount(0);
    var gen1 = GC.CollectionCount(1);
    var gen2 = GC.CollectionCount(2);
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    timer.Start();
    var runtime = new DmRuntime(project);
    timer.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    var gen0Delta = GC.CollectionCount(0) - gen0;
    var gen1Delta = GC.CollectionCount(1) - gen1;
    var gen2Delta = GC.CollectionCount(2) - gen2;
    var immediateManaged = GC.GetTotalMemory(false);
    // Collection is deliberately outside the constructor timer/allocation interval.
    FullCollection();
    var retainedAfter = GC.GetTotalMemory(false);
    GC.KeepAlive(runtime);
    GC.KeepAlive(project);
    // Validate and enumerate after measuring, so observer allocations aren't attributed.
    var cells = runtime.MapCells;
    var turfs = cells.Select(c => c.Turf).Distinct().Count();
    var areas = cells.Select(c => c.Area).Distinct().Count();
    var variables = cells.Select(c => c.Turf.Variables.Count).Distinct().ToArray();
    if (runtime.Width != side || runtime.Height != side || runtime.Levels != 1 ||
        cells.Count != side * side || turfs != cells.Count || areas != 1 ||
        runtime.MapEntities.Count != 0 || variables.Length != 1 || variables[0] != fields + 2)
        throw new InvalidOperationException("Synthetic runtime topology or inherited-field count differs from the case.");
    using var process = Process.GetCurrentProcess();
    return new() {
        ["construction_elapsed_ms"] = timer.Elapsed.TotalMilliseconds,
        ["construction_process_managed_allocated_bytes"] = allocated,
        ["construction_gen0_collections"] = gen0Delta,
        ["construction_gen1_collections"] = gen1Delta,
        ["construction_gen2_collections"] = gen2Delta,
        ["managed_bytes_immediately_after_construction"] = immediateManaged,
        ["managed_bytes_before_construction_after_forced_gc"] = retainedBefore,
        ["managed_bytes_after_construction_after_forced_gc"] = retainedAfter,
        ["retained_managed_delta_bytes_after_forced_gc"] = retainedAfter - retainedBefore,
        ["working_set_bytes_after_forced_gc_and_validation"] = process.WorkingSet64,
        ["process_peak_working_set_bytes"] = process.PeakWorkingSet64,
        ["actual_turf_count"] = turfs, ["actual_area_count"] = areas,
        ["actual_entity_count"] = runtime.MapEntities.Count,
        ["actual_turf_variable_count"] = variables[0]
    };
}
