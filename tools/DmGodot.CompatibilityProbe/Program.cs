using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DmGodot.Runtime;

// A bounded compatibility measurement. This executable has no Host reference,
// network listener, world ticking loop, native service adapter or save access.
const int globalDispatchBatchSize = 256;
var globalProfile = DmGlobalInitializationProfile.Standard;
var phase = "artifact_load";
var status = "unexpected_error";
string? artifactHash = null;
string? inventoryHash = null;
string? mapIconHash = null;
Exception? failure = null;
int? pendingThreads = null;
int? logCount = null;
DmGlobalWork? globalWork = null;
var elapsed = Stopwatch.StartNew();
var exitCode = 1;
try {
    if (args.Length > 0 && args[0] == "--extended-globals") {
        globalProfile = DmGlobalInitializationProfile.Extended;
        args = args[1..];
    }
    if (Array.IndexOf(args, "--extended-globals") >= 0)
        throw new ArgumentException("--extended-globals may appear only once, before the artifact.");
    string? fileManifest = null, contentAllowlist = null, iconManifest = null;
    if (args.Length != 1) {
        if (args.Length is not (7 or 9 or 11) || args[3] != "--source-revision" || args[5] != "--source-tree")
            throw new ArgumentException("Expected an artifact and optional source-bound snapshot arguments.");
        switch (args[1]) {
            case "--file-inventory": fileManifest = args[2]; break;
            case "--map-icons": iconManifest = args[2]; break;
            default: throw new ArgumentException("Unknown snapshot option.");
        }
        for (var i = 7; i < args.Length; i += 2) {
            switch (args[i]) {
                case "--content-allowlist" when fileManifest is not null && contentAllowlist is null:
                    contentAllowlist = args[i + 1]; break;
                case "--map-icons" when iconManifest is null:
                    iconManifest = args[i + 1]; break;
                default: throw new ArgumentException("Unknown, duplicated or inapplicable snapshot option.");
            }
        }
    }
    var path = Path.GetFullPath(args[0]);
    var bytes = File.ReadAllBytes(path);
    artifactHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var project = CompiledProject.Parse(System.Text.Encoding.UTF8.GetString(bytes), path);
    DmFileInventory? inventory = null;
    DmTextFileSnapshot? textFiles = null;
    if (fileManifest is not null) {
        // Only this trusted host executable reads the explicitly selected manifest.
        // The runtime receives owned names, never a host path or filesystem callback.
        using var stream = File.OpenRead(fileManifest);
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
        if (contentAllowlist is null && (root.TryGetProperty("contents", out _) || root.TryGetProperty("content_allowlist_sha256", out _)))
            throw new ArgumentException("Text snapshot requires a separately supplied trusted allowlist.");
        ExactKeys(root, contentAllowlist is not null
            ? ["schema", "scope", "source_revision", "source_tree", "artifact_sha256", "files", "blocked_paths", "content_allowlist_sha256", "contents"]
            : ["schema", "scope", "source_revision", "source_tree", "artifact_sha256", "files", "blocked_paths"]);
        if (!MatchesSource(root, "tracked-source-filesystem-snapshot", args[4], args[6], artifactHash))
            throw new ArgumentException("File inventory source/artifact binding mismatch.");
        try {
            inventory = ReadFileInventory(root);
        } catch (CompatibilityException error) {
            // Invalid host configuration is a failed measurement, not DM incompatibility.
            throw new ArgumentException("Invalid file inventory configuration: " + error.Message, error);
        }

        if (contentAllowlist is not null) {
            var allowedBytes = ReadSmallManifest(contentAllowlist, "Content allowlist");
            using var allowedDocument = JsonDocument.Parse(allowedBytes);
            var allowedRoot = allowedDocument.RootElement;
            var allowedHash = Convert.ToHexString(SHA256.HashData(allowedBytes)).ToLowerInvariant();

            ExactKeys(allowedRoot, "schema", "source_revision", "source_tree", "paths", "blobs");
            if (allowedRoot.GetProperty("schema").GetInt32() != 1 ||
                allowedRoot.GetProperty("source_revision").GetString() != args[4] ||
                allowedRoot.GetProperty("source_tree").GetString() != args[6] ||
                root.GetProperty("content_allowlist_sha256").GetString() != allowedHash)
                throw new ArgumentException("Text snapshot trusted allowlist binding mismatch.");
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var expectedPaths = allowedRoot.GetProperty("paths");
            if (expectedPaths.GetArrayLength() is < 1 or > DmTextFileSnapshot.MaximumFiles)
                throw new ArgumentException("Invalid trusted content allowlist count.");
            foreach (var entry in expectedPaths.EnumerateArray()) {
                var name = entry.GetString()!;
                if (string.IsNullOrEmpty(name)) throw new ArgumentException("Invalid trusted content allowlist path.");
                try { DmFileInventory.ValidatePath(name, allowDirectory: false); }
                catch (CompatibilityException error) { throw new ArgumentException("Invalid trusted content allowlist path.", error); }
                if (!allowed.Add(name)) throw new ArgumentException("Invalid trusted content allowlist duplicate.");
            }
            var expectedBlobs = allowedRoot.GetProperty("blobs");
            ExactKeys(expectedBlobs, allowed.ToArray());
            long expectedTotal = 0;
            foreach (var name in allowed) {
                var expectedBlob = expectedBlobs.GetProperty(name);
                ExactKeys(expectedBlob, "oid", "bytes", "sha256");
                var expectedLength = expectedBlob.GetProperty("bytes").GetInt32();
                if (expectedLength < 0 || expectedLength > DmTextFileSnapshot.MaximumFileBytes ||
                    (expectedTotal += expectedLength) > DmTextFileSnapshot.MaximumTotalBytes ||
                    !Digest(expectedBlob.GetProperty("oid").GetString(), 40) ||
                    !Digest(expectedBlob.GetProperty("sha256").GetString(), 64))
                    throw new ArgumentException("Invalid trusted content blob bounds/digest.");
            }
            var contentEntries = root.GetProperty("contents");
            if (contentEntries.GetArrayLength() != allowed.Count)
                throw new ArgumentException("Text snapshot does not cover the trusted allowlist.");
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long rawTotal = 0;
            foreach (var entry in contentEntries.EnumerateArray()) {
                ExactKeys(entry, "path", "blob_id", "byte_length", "sha256", "base64");
                var name = entry.GetProperty("path").GetString()!;
                if (!allowed.Contains(name) || contents.ContainsKey(name))
                    throw new ArgumentException("Text snapshot entry is outside trusted allowlist or duplicated.");
                var expectedBlob = expectedBlobs.GetProperty(name);
                var length = entry.GetProperty("byte_length").GetInt32();
                if (length != expectedBlob.GetProperty("bytes").GetInt32() ||
                    entry.GetProperty("blob_id").GetString() != expectedBlob.GetProperty("oid").GetString() ||
                    entry.GetProperty("sha256").GetString() != expectedBlob.GetProperty("sha256").GetString())
                    throw new ArgumentException("Text snapshot differs from trusted source blob binding.");
                var encoded = entry.GetProperty("base64").GetString()!;
                if (encoded.Length != 4 * ((length + 2) / 3) ||
                    rawTotal + length > DmTextFileSnapshot.MaximumTotalBytes)
                    throw new ArgumentException("Text snapshot encoded/raw byte limit exceeded.");
                var raw = Convert.FromBase64String(encoded);
                var hash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
                var header = System.Text.Encoding.ASCII.GetBytes("blob " + raw.Length + "\0");
                using var blobHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                blobHasher.AppendData(header);
                blobHasher.AppendData(raw);
                var blobId = Convert.ToHexString(blobHasher.GetHashAndReset()).ToLowerInvariant();
                if (raw.Length != length || hash != expectedBlob.GetProperty("sha256").GetString() ||
                    blobId != expectedBlob.GetProperty("oid").GetString() || Convert.ToBase64String(raw) != encoded)
                    throw new ArgumentException("Text snapshot blob hash/length mismatch.");
                rawTotal += raw.Length;
                contents.Add(name, raw);
            }
            if (!allowed.SetEquals(contents.Keys)) throw new ArgumentException("Text snapshot does not cover the trusted allowlist.");
            try { textFiles = new DmTextFileSnapshot(inventory!, contents); }
            catch (CompatibilityException error) {
                throw new ArgumentException("Invalid text snapshot configuration: " + error.Message, error);
            }
        } else if (root.TryGetProperty("contents", out _)) {
            throw new ArgumentException("Text snapshot requires a separately supplied trusted allowlist.");
        }
    }
    DmMapIconInventory? mapIcons = null;
    if (iconManifest is not null) {
        // The host verifies each regular Git blob, its digests and its DMI kind
        // before issuing this manifest. The probe checks the attestation's shape
        // and source/project binding. It never opens or decodes image bytes.
        var iconBytes = ReadSmallManifest(iconManifest, "Map icon inventory");
        mapIconHash = Convert.ToHexString(SHA256.HashData(iconBytes)).ToLowerInvariant();
        using var document = JsonDocument.Parse(iconBytes);
        var root = document.RootElement;
        ExactKeys(root, "schema", "scope", "source_revision", "source_tree", "artifact_sha256",
            "files", "blocked_paths", "bindings");
        if (!MatchesSource(root, "tracked-map-icon-bindings", args[4], args[6], artifactHash))
            throw new ArgumentException("Map icon inventory source/artifact binding mismatch.");
        try {
            var assets = ReadFileInventory(root);
            var bindings = root.GetProperty("bindings").EnumerateArray().Select(entry => {
                ExactKeys(entry, "index", "token", "path", "oid", "bytes", "sha256");
                if (entry.GetProperty("bytes").GetInt64() <= 0 ||
                    !Digest(entry.GetProperty("oid").GetString(), 40) || !Digest(entry.GetProperty("sha256").GetString(), 64))
                    throw new ArgumentException("Invalid map icon source blob attestation.");
                return new DmMapIconBinding(entry.GetProperty("index").GetInt32(),
                    entry.GetProperty("token").GetString()!, entry.GetProperty("path").GetString()!);
            });
            mapIcons = new DmMapIconInventory(project, assets, bindings);
        } catch (CompatibilityException error) {
            throw new ArgumentException("Invalid map icon inventory configuration: " + error.Message, error);
        }
    }
    phase = "runtime_construct";
    using var session = new DmStartupSession(project, inventory, textFiles, default, mapIcons, globalProfile);
    phase = "boot";
    try {
        while (!session.AdvanceGlobals(globalDispatchBatchSize)) {
            // Resume the same global initializer without advancing virtual time.
        }
        _ = session.Advance() ?? throw new InvalidOperationException("Startup session did not complete after global initialization.");
        status = "boot_returned";
        exitCode = 0;
    } finally {
        globalWork = session.GlobalWork;
        pendingThreads = session.PendingThreads;
        logCount = session.LogCount;
    }
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
    map_icon_inventory_sha256 = mapIconHash,
    instruction_budget = DmRuntime.DefaultInstructionBudget,
    global_work_budget = (int)globalProfile,
    global_step_work = globalWork?.StepSpent,
    global_work_remaining = globalWork?.Remaining,
    configured_gc_heap_hard_limit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
    gc_total_available_memory_bytes = gcAvailable,
    gc_heap_size_bytes_last_collection = gcHeap,
    gc_memory_load_bytes_last_collection = gcLoad,
    managed_memory_bytes = managedMemory,
    working_set_bytes = workingSet,
    peak_working_set_bytes = peakWorkingSet,
    memory_measurement_error = memoryMeasurementError,
    elapsed_ms = elapsed.ElapsedMilliseconds,
    pending_threads = pendingThreads,
    log_count = logCount,
    exception_type = failure?.GetType().FullName,
    message = failure?.Message
}));
return exitCode;


static byte[] ReadSmallManifest(string path, string label) {
    using var stream = File.OpenRead(path);
    var length = stream.Length;
    if (length > 128 * 1024) throw new ArgumentException(label + " size limit exceeded.");
    var bytes = new byte[checked((int)length)];
    stream.ReadExactly(bytes);
    if (stream.ReadByte() != -1) throw new ArgumentException(label + " changed during reading.");
    return bytes;
}

static DmFileInventory ReadFileInventory(JsonElement root) => new(
    root.GetProperty("files").EnumerateArray().Select(value => value.GetString()!),
    root.GetProperty("blocked_paths").EnumerateArray().Select(value => value.GetString()!));

static bool MatchesSource(JsonElement root, string scope, string revision, string tree, string artifact) =>
    root.GetProperty("schema").GetInt32() == 1 && root.GetProperty("scope").GetString() == scope &&
    revision.Length == 40 && tree.Length == 40 && revision.All(Uri.IsHexDigit) && tree.All(Uri.IsHexDigit) &&
    root.GetProperty("source_revision").GetString() == revision &&
    root.GetProperty("source_tree").GetString() == tree && root.GetProperty("artifact_sha256").GetString() == artifact;

static bool Digest(string? text, int length) =>
    text?.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

static void ExactKeys(JsonElement value, params string[] expected) {
    var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
    if (value.ValueKind != JsonValueKind.Object)
        throw new ArgumentException("Invalid snapshot configuration object.");
    foreach (var property in value.EnumerateObject())
        if (!remaining.Remove(property.Name))
            throw new ArgumentException("Unknown or duplicate snapshot configuration property.");
    if (remaining.Count != 0) throw new ArgumentException("Missing snapshot configuration property.");
}
