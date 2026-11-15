using System.Text.Json;
using DMCompiler.Bytecode;

namespace DmGodot.Runtime;

/// <summary>Immutable compiler input. The wire version is the actual OpenDream opcode-table hash.</summary>
public sealed class CompiledProject {
    public const string CompilerRevision = "846775a06b8fa4639772cb3e759adcc0133d9711";
    public static string SupportedBytecodeVersion => OpcodeVerifier.GetOpcodesHash();
    internal CompiledData Data { get; }
    public string BytecodeVersion => Data.Metadata.Version;
    public int TypeCount => Data.Types.Length;
    public int ProcedureCount => Data.Procs.Length;
    public string SourcePath { get; }

    private CompiledProject(CompiledData data, string path) {
        Data = data;
        SourcePath = path;
        if (data.Metadata is null || data.Types is null || data.Procs is null || data.Strings is null)
            throw new CompatibilityException("Compiler artifact is missing required metadata, types, procedures, or strings.");
        if (!string.Equals(data.Metadata.Version, SupportedBytecodeVersion, StringComparison.Ordinal))
            throw new CompatibilityException($"Unsupported OpenDream bytecode version '{data.Metadata.Version}'; expected '{SupportedBytecodeVersion}' from {CompilerRevision}.");
        ValidateStructure();
        if (data.Types.Length == 0 || data.Types.Select(t => t.Path).Distinct().Count() != data.Types.Length)
            throw new CompatibilityException("Compiler artifact has no types or duplicate type paths.");
        for (var i = 0; i < data.Types.Length; i++) {
            var seen = new HashSet<int>();
            for (int? current = i; current is not null; current = data.Types[current.Value].Parent) {
                if (current < 0 || current >= data.Types.Length || !seen.Add(current.Value))
                    throw new CompatibilityException($"Invalid or cyclic type parent for type {i}.");
            }
            var type = data.Types[i];
            foreach (var id in (type.Procs ?? []).SelectMany(p => p).Concat(type.InitProc is int init ? [init] : []))
                CheckProc(id);
        }
        foreach (var id in data.GlobalProcs ?? []) CheckProc(id);
        foreach (var proc in data.Procs.Append(data.GlobalInitProc).OfType<Procedure>()) {
            if (proc.OwningTypeId < 0 || proc.OwningTypeId >= data.Types.Length || proc.MaxVariableId < 0 || proc.MaxVariableId > 65536)
                throw new CompatibilityException($"Invalid procedure metadata for '{proc.Name}'.");
            ValidateBytecode(proc);
        }
    }

    // Required properties in System.Text.Json may still contain explicit JSON nulls.
    // Validate all nested collections before LINQ, indexing, map loading, or diagnostics use them.
    private void ValidateStructure() {
        if (Data.Strings.Any(text => text is null)) Invalid("Strings contains null");
        if (Data.Types.Any(type => type is null || string.IsNullOrEmpty(type.Path))) Invalid("Types contains a null entry or path");
        if (Data.Procs.Any(proc => proc is null || string.IsNullOrEmpty(proc.Name))) Invalid("Procs contains a null entry or name");
        foreach (var type in Data.Types) {
            if (type.Procs?.Any(chain => chain is null) == true) Invalid($"Type {type.Path} has a null procedure chain");
            if (type.ConstVariables?.Any(name => string.IsNullOrEmpty(name)) == true)
                Invalid($"Type {type.Path} has a null or empty const variable name");
            ValidateValues(type.Variables, $"type {type.Path}");
            foreach (var (name, index) in type.GlobalVariables ?? [])
                if (string.IsNullOrEmpty(name) || index < 0 || index >= (Data.Globals?.GlobalCount ?? 0))
                    Invalid($"Type {type.Path} has an invalid static/global variable mapping");
        }
        foreach (var proc in Data.Procs.Append(Data.GlobalInitProc).OfType<Procedure>()) {
            if (string.IsNullOrEmpty(proc.Name)) Invalid("Procedure name is null or empty");
            if (proc.Arguments?.Any(arg => arg is null || string.IsNullOrEmpty(arg.Name)) == true) Invalid($"Procedure {proc.Name} has a null argument or name");
            if (proc.SourceInfo is null || proc.SourceInfo.Any(info => info is null)) Invalid($"Procedure {proc.Name} has a null source map or entry");
            foreach (var info in proc.SourceInfo!) {
                if (info.Offset < 0 || info.Line < 0 || (info.File is int file && (file < 0 || file >= Data.Strings.Length)))
                    Invalid($"Procedure {proc.Name} has invalid source metadata");
            }
        }
        if (Data.Globals is { } globals) {
            if (globals.Names is null || globals.GlobalValues is null) Invalid("Globals contains a null required collection");
            if (globals.GlobalCount < 0 || globals.Names!.Length != globals.GlobalCount || globals.Names.Any(name => name is null)) Invalid("Globals has invalid names or count");
            foreach (var (id, value) in globals.GlobalValues!) {
                if (id < 0 || id >= globals.GlobalCount) Invalid("Globals has an invalid value index");
                ValidateValue(value, $"global {id}");
            }
        }
        foreach (var map in Data.Maps ?? []) {
            if (map is null || map.CellDefinitions is null || map.Blocks is null) Invalid("Maps contains a null map or required collection");
            foreach (var (key, cell) in map!.CellDefinitions!) {
                if (cell is null || cell.Objects is null || cell.Objects.Any(obj => obj is null)) Invalid($"Map cell {key} contains a null definition, objects collection, or object");
                foreach (var obj in cell!.Objects!.Concat(new[] { cell.Turf, cell.Area }).OfType<MapObject>()) {
                    if (obj.Type < 0 || obj.Type >= Data.Types.Length) Invalid($"Map cell {key} has an invalid type ID");
                    ValidateValues(obj.VarOverrides, $"map cell {key}");
                }
            }
            foreach (var block in map.Blocks!) {
                if (block is null || block.Cells is null || block.Cells.Any(key => key is null)) Invalid("Map block contains a null block, cells collection, or cell key");
            }
        }
    }
    private static void Invalid(string reason) => throw new CompatibilityException($"Malformed compiler artifact: {reason}.");
    private void ValidateValues(Dictionary<string, JsonElement>? values, string context) {
        foreach (var (name, value) in values ?? []) ValidateValue(value, $"{context}.{name}");
    }
    private void ValidateValue(JsonElement value, string context) {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number) return;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var tag) || tag.ValueKind != JsonValueKind.Number || !tag.TryGetInt32(out var kind)) {
            Invalid($"{context} has an invalid tagged value"); return;
        }
        switch (kind) {
            case 0:
                if (!value.TryGetProperty("resourcePath", out var resource) || resource.ValueKind != JsonValueKind.String) Invalid($"{context} has a null or invalid resource path");
                break;
            case 1: case 2:
                if (!value.TryGetProperty("value", out var target) || target.ValueKind != JsonValueKind.Number || !target.TryGetInt32(out var id) || id < 0 || id >= (kind == 1 ? Data.Types.Length : Data.Procs.Length)) Invalid($"{context} has an invalid tagged reference");
                break;
            case 4: case 5: break;
            case 3: case 6:
                // Lists are valid compiler data but remain an explicit runtime capability gap.
                if (!value.TryGetProperty("values", out var entries) || entries.ValueKind != JsonValueKind.Array) Invalid($"{context} has an invalid tagged list");
                break;
            default: Invalid($"{context} has unknown value tag {kind}"); break;
        }
    }

    public static CompiledProject Load(string path) => Parse(File.ReadAllText(path), Path.GetFullPath(path));
    public static CompiledProject Parse(string json, string sourcePath = "<memory>") {
        try {
            var data = JsonSerializer.Deserialize<CompiledData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new CompatibilityException("Empty compiler artifact.");
            return new(data, sourcePath);
        } catch (JsonException ex) { throw new CompatibilityException($"Invalid OpenDream compiler JSON: {ex.Message}", ex); }
    }

    private void CheckProc(int id) {
        if (id < 0 || id >= Data.Procs.Length) throw new CompatibilityException($"Invalid procedure index {id}.");
    }
    internal string String(int id) => id >= 0 && id < Data.Strings.Length ? Data.Strings[id] : throw new CompatibilityException($"Invalid string index {id}.");
    internal string Location(Procedure proc, int offset) {
        string file = SourcePath;
        var line = 0;
        foreach (var item in proc.SourceInfo ?? []) {
            if (item.Offset > offset) break;
            if (item.File is int f) file = String(f);
            line = item.Line;
        }
        return $"{file}:{line} ({Data.Types[proc.OwningTypeId].Path}/{proc.Name}, byte {offset})";
    }

    private void ValidateBytecode(Procedure proc) {
        if (proc.Bytecode is not { Length: > 0 } bytes) return;
        var reader = new ByteReader(bytes);
        var boundaries = new HashSet<int>();
        var jumps = new List<int>();
        try {
            while (reader.Position < bytes.Length) {
                var offset = reader.Position;
                boundaries.Add(offset);
                var opcode = (DreamProcOpcode)reader.Byte();
                if (!Enum.IsDefined(opcode)) throw new CompatibilityException($"Unknown opcode 0x{(byte)opcode:X2} at {Location(proc, offset)}.");
                var meta = OpcodeMetadataCache.GetMetadata(opcode);
                if (meta.VariableArgs) {
                    var count = reader.Int();
                    if (count < 0 || count > bytes.Length) throw new CompatibilityException("Invalid variable operand count.");
                    for (var i = 0; i < count; i++) {
                        switch (opcode) {
                            case DreamProcOpcode.PushNRefs: case DreamProcOpcode.CreateListNRefs: reader.Reference(); break;
                            case DreamProcOpcode.NPushFloatAssign: reader.Float(); reader.Reference(); break;
                            case DreamProcOpcode.PushNOfStringFloats: String(reader.Int()); reader.Float(); break;
                            case DreamProcOpcode.PushNStrings: case DreamProcOpcode.PushNResources:
                            case DreamProcOpcode.CreateListNStrings: case DreamProcOpcode.CreateListNResources: String(reader.Int()); break;
                            case DreamProcOpcode.PushNFloats: case DreamProcOpcode.CreateListNFloats: reader.Float(); break;
                            default: throw new CompatibilityException($"Unknown variable encoding for {opcode}.");
                        }
                    }
                } else foreach (var operand in meta.RequiredArgs) {
                    switch (operand) {
                        case OpcodeArgType.ArgType: reader.Byte(); break;
                        case OpcodeArgType.Reference: reader.Reference(); break;
                        case OpcodeArgType.Float: reader.Float(); break;
                        case OpcodeArgType.String: case OpcodeArgType.Resource: String(reader.Int()); break;
                        case OpcodeArgType.Label: jumps.Add(reader.Int()); break;
                        default: reader.Int(); break;
                    }
                }
            }
            boundaries.Add(bytes.Length);
            if (jumps.Any(j => !boundaries.Contains(j))) throw new CompatibilityException($"Jump target is not an instruction boundary in {proc.Name}.");
        } catch (CompatibilityException ex) { throw new CompatibilityException($"{ex.Message} [{Location(proc, reader.Position)}]", ex); }
    }
}

public sealed class CompatibilityException : Exception {
    public CompatibilityException(string message, Exception? inner = null) : base(message, inner) { }
}

internal sealed class CompiledData {
    public required Metadata Metadata { get; init; }
    public required string[] Strings { get; init; }
    public required TypeDefinition[] Types { get; init; }
    public required Procedure[] Procs { get; init; }
    public int[]? GlobalProcs { get; init; }
    public Globals? Globals { get; init; }
    public Procedure? GlobalInitProc { get; init; }
    public MapDefinition[]? Maps { get; init; }
}
internal sealed class Metadata { public required string Version { get; init; } }
internal sealed class TypeDefinition {
    public required string Path { get; init; }
    public int? Parent { get; init; }
    public int? InitProc { get; init; }
    public string[]? ConstVariables { get; init; }
    public Dictionary<string, int>? GlobalVariables { get; init; }
    public int[][]? Procs { get; init; }
    public Dictionary<string, JsonElement>? Variables { get; init; }
}
internal sealed class Procedure {
    public required string Name { get; init; }
    public int OwningTypeId { get; init; }
    public int Attributes { get; init; }
    public int MaxVariableId { get; init; }
    public Argument[]? Arguments { get; init; }
    public SourceInfo[]? SourceInfo { get; init; }
    public byte[]? Bytecode { get; init; }
}
internal sealed class Argument { public required string Name { get; init; } }
internal sealed class SourceInfo { public int Offset { get; init; } public int? File { get; init; } public int Line { get; init; } }
internal sealed class Globals {
    public int GlobalCount { get; init; }
    public string[] Names { get; init; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("Globals")]
    public Dictionary<int, JsonElement> GlobalValues { get; init; } = [];
}
internal sealed class MapDefinition {
    public int MaxX { get; init; }
    public int MaxY { get; init; }
    public int MaxZ { get; init; }
    public Dictionary<string, MapCell> CellDefinitions { get; init; } = [];
    public MapBlock[] Blocks { get; init; } = [];
}
internal sealed class MapCell { public MapObject? Turf { get; init; } public MapObject? Area { get; init; } public MapObject[] Objects { get; init; } = []; }
internal sealed class MapObject { public int Type { get; init; } public Dictionary<string, JsonElement>? VarOverrides { get; init; } }
internal sealed class MapBlock {
    public int X { get; init; } public int Y { get; init; } public int Z { get; init; }
    public int Width { get; init; } public int Height { get; init; }
    public string[] Cells { get; init; } = [];
}
internal readonly record struct Reference(byte Kind, int Value = 0);
internal sealed class ByteReader(byte[] bytes) {
    public int Position { get; set; }
    public int Length => bytes.Length;
    private void Ensure(int count) { if (Position < 0 || Position > bytes.Length - count) throw new CompatibilityException("Truncated bytecode operand."); }
    public byte Byte() { Ensure(1); return bytes[Position++]; }
    public int Int() { Ensure(4); var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Position, 4)); Position += 4; return value; }
    public float Float() => BitConverter.Int32BitsToSingle(Int());
    public Reference Reference() {
        var kind = Byte();
        return kind switch {
            >= 1 and <= 7 or 15 or 16 => new(kind),
            8 or 9 => new(kind, Byte()),
            >= 10 and <= 14 => new(kind, Int()),
            _ => throw new CompatibilityException($"Invalid reference kind {kind}.")
        };
    }
}
