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
Console.WriteLine(JsonSerializer.Serialize(new {
    schema = 1,
    scope = "Load, construct and Boot() only; no ticking, network, gameplay or persistence acceptance.",
    runtime_status = status,
    phase,
    artifact_sha256 = artifactHash,
    instruction_budget = instructionBudget,
    elapsed_ms = elapsed.ElapsedMilliseconds,
    pending_threads = runtime?.PendingThreads,
    log_count = runtime?.Logs.Count,
    exception_type = failure?.GetType().FullName,
    message = failure?.Message
}));
return exitCode;
