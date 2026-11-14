using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DmGodot.Runtime;

// A bounded compatibility measurement. This executable has no Host reference,
// network listener, world ticking loop, native service adapter or save access.
const int instructionBudget = 100_000;
var phase = "artifact_load";
var status = "unexpected_error";
string? artifactHash = null;
Exception? failure = null;
DmRuntime? runtime = null;
var elapsed = Stopwatch.StartNew();
var exitCode = 1;
try {
    if (args.Length != 1) throw new ArgumentException("Expected one compiled artifact path.");
    var path = Path.GetFullPath(args[0]);
    var bytes = File.ReadAllBytes(path);
    artifactHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var project = CompiledProject.Parse(System.Text.Encoding.UTF8.GetString(bytes), path);
    phase = "runtime_construct";
    runtime = new DmRuntime(project) { InstructionBudget = instructionBudget };
    phase = "boot";
    runtime.Boot();
    status = "boot_returned";
    exitCode = 0;
} catch (CompatibilityException error) {
    failure = error;
    status = "blocked";
    exitCode = 3;
} catch (Exception error) {
    failure = error;
}
// Snapshot after the attempt without forcing a collection. GC heap/load describe
// the last collection; process working set includes memory outside the GC heap.
long? gcAvailable = null, gcHeap = null, gcLoad = null, managedMemory = null, workingSet = null, peakWorkingSet = null;
string? memoryMeasurementError = null;
try {
    var memory = GC.GetGCMemoryInfo();
    gcAvailable = memory.TotalAvailableMemoryBytes;
    gcHeap = memory.HeapSizeBytes;
    gcLoad = memory.MemoryLoadBytes;
    managedMemory = GC.GetTotalMemory(forceFullCollection: false);
    using var process = Process.GetCurrentProcess();
    workingSet = process.WorkingSet64;
    peakWorkingSet = process.PeakWorkingSet64;
} catch (Exception error) {
    // Best-effort diagnostics must not replace the original runtime outcome.
    memoryMeasurementError = error.GetType().FullName;
}

Console.WriteLine(JsonSerializer.Serialize(new {
    schema = 1,
    scope = "Load, construct and Boot() only; no ticking, network, gameplay or persistence acceptance.",
    runtime_status = status,
    phase,
    artifact_sha256 = artifactHash,
    instruction_budget = instructionBudget,
    configured_gc_heap_hard_limit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
    gc_total_available_memory_bytes = gcAvailable,
    gc_heap_size_bytes_last_collection = gcHeap,
    gc_memory_load_bytes_last_collection = gcLoad,
    managed_memory_bytes = managedMemory,
    working_set_bytes = workingSet,
    peak_working_set_bytes = peakWorkingSet,
    memory_measurement_error = memoryMeasurementError,
    elapsed_ms = elapsed.ElapsedMilliseconds,
    pending_threads = runtime?.PendingThreads,
    log_count = runtime?.Logs.Count,
    exception_type = failure?.GetType().FullName,
    message = failure?.Message
}));
return exitCode;
