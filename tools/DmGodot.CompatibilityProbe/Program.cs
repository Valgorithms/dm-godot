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
string? inventoryHash = null;
Exception? failure = null;
DmRuntime? runtime = null;
var elapsed = Stopwatch.StartNew();
var exitCode = 1;
try {
    if (args.Length != 1 && (args.Length != 7 || args[1] != "--file-inventory" ||
        args[3] != "--source-revision" || args[5] != "--source-tree"))
        throw new ArgumentException("Expected artifact path and optional explicit file-inventory/source binding arguments.");
    var path = Path.GetFullPath(args[0]);
    var bytes = File.ReadAllBytes(path);
    artifactHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var project = CompiledProject.Parse(System.Text.Encoding.UTF8.GetString(bytes), path);
    DmFileInventory? inventory = null;
    if (args.Length == 7) {
        // Only this trusted host executable reads the explicitly selected manifest.
        // The runtime receives owned names, never a host path or filesystem callback.
        using var stream = File.OpenRead(args[2]);
        const int manifestLimit = 32 * 1024 * 1024;
        if (stream.Length > manifestLimit) throw new ArgumentException("File inventory manifest exceeds size limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk)) > 0) {
            if (buffer.Length + count > manifestLimit) throw new ArgumentException("File inventory manifest exceeds size limit.");
            buffer.Write(chunk, 0, count);
        }
        var manifestBytes = buffer.ToArray();
        inventoryHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 ||
            root.GetProperty("scope").GetString() != "tracked-source-filesystem-snapshot" ||
            root.GetProperty("source_revision").GetString() != args[4] ||
            root.GetProperty("source_tree").GetString() != args[6] ||
            root.GetProperty("artifact_sha256").GetString() != artifactHash ||
            args[4].Length != 40 || args[6].Length != 40 ||
            !args[4].All(Uri.IsHexDigit) || !args[6].All(Uri.IsHexDigit))
            throw new ArgumentException("File inventory source/artifact binding mismatch.");
        try {
            inventory = new DmFileInventory(
                root.GetProperty("files").EnumerateArray().Select(value => value.GetString()!),
                root.GetProperty("blocked_paths").EnumerateArray().Select(value => value.GetString()!));
        } catch (CompatibilityException error) {
            // Invalid host configuration is a failed measurement, not DM incompatibility.
            throw new ArgumentException("Invalid file inventory configuration: " + error.Message, error);
        }
    }
    phase = "runtime_construct";
    runtime = new DmRuntime(project, inventory) { InstructionBudget = instructionBudget };
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
    file_inventory_sha256 = inventoryHash,
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
