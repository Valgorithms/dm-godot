using System.Globalization;
using System.Text.Json;

namespace DmGodot.Runtime;

/// <summary>DM values keep float32 numbers and reference identity for objects/lists.</summary>
public readonly record struct DmValue(object? Value) {
    public static readonly DmValue Null = new(null);
    public bool IsNull => Value is null;
    public bool IsTruthy => Value switch { null => false, float n => n != 0, string s => s.Length != 0, _ => true };
    public float Number => Value switch { null => 0, float n => n, _ => throw new CompatibilityException($"Expected a DM number, got {Value?.GetType().Name}.") };
    public override string ToString() => Value switch {
        null => "", float n => n.ToString("G9", CultureInfo.InvariantCulture), string s => s,
        DmTypeReference t => t.Path, DmResource r => r.Path,
        DmObject { IsNativeMatrix: true } => throw new CompatibilityException("Matrix stringification is not implemented."),
        DmObject o => o.TryGetVariable("name", out var n) && !n.IsNull ? n.ToString() : o.TypePath,
        DmList => throw new CompatibilityException("List stringification is not implemented."),
        DmAssocList => throw new CompatibilityException("Strict associative list stringification is not implemented."),
        _ => throw new CompatibilityException($"Unsupported DM value {Value.GetType().Name}.")
    };
    public static implicit operator DmValue(float value) => new(value);
    public static implicit operator DmValue(int value) => new((float)value);
    public static implicit operator DmValue(string value) => new(value);
}
public sealed record DmTypeReference(int Id, string Path);
public sealed record DmResource(string Path);
public sealed class DmObject {
    internal bool IsNativeMatrix { get; private init; }
    public int TypeId { get; }
    public string TypePath { get; }
    private Dictionary<string, DmValue>? _variables;
    private IReadOnlyDictionary<string, DmValue>? _defaults;
    private readonly DmValue _type;
    private readonly DmValue _parentType;

    // Preserve the public mutable Dictionary API and stable alias identity. Once
    // exposed or written, this dictionary is authoritative, even after Clear/Remove.
    public Dictionary<string, DmValue> Variables => MaterializeVariables();

    public DmObject(int typeId, string typePath) {
        TypeId = typeId; TypePath = typePath;
        _variables = new(StringComparer.Ordinal);
    }
    internal DmObject(int typeId, string typePath, IReadOnlyDictionary<string, DmValue> defaults,
        DmValue type, DmValue parentType) {
        TypeId = typeId; TypePath = typePath;
        _defaults = defaults;
        _type = type;
        _parentType = parentType;
    }
    internal static DmObject CreateNativeMatrix(int id, IReadOnlyDictionary<string, DmValue> defaults,
        DmValue type, DmValue parentType) => new(id, "/matrix", defaults, type, parentType) { IsNativeMatrix = true };

    private Dictionary<string, DmValue> MaterializeVariables() {
        if (_variables is not null) return _variables;
        var defaults = _defaults!;
        var count = defaults.Count + (defaults.ContainsKey("type") ? 0 : 1) + (defaults.ContainsKey("parent_type") ? 0 : 1);
        var variables = new Dictionary<string, DmValue>(count, StringComparer.Ordinal);
        foreach (var pair in defaults) variables.Add(pair.Key, pair.Value);
        // Existing metadata keys keep their positions; missing keys append in order.
        variables["type"] = _type;
        variables["parent_type"] = _parentType;
        _variables = variables;
        _defaults = null;
        return variables;
    }
    internal bool TryGetVariable(string name, out DmValue value) {
        if (_variables is not null) return _variables.TryGetValue(name, out value);
        if (name == "type") { value = _type; return true; }
        if (name == "parent_type") { value = _parentType; return true; }
        return _defaults!.TryGetValue(name, out value);
    }
    internal DmValue GetVariableOrDefault(string name) => TryGetVariable(name, out var value) ? value : DmValue.Null;
    internal bool HasVariable(string name) => TryGetVariable(name, out _);
    internal void SetVariable(string name, DmValue value) => MaterializeVariables()[name] = value;
}
/// <summary>Ordered list entries plus associative keys, distinct from a host dictionary.</summary>
public sealed class DmList {
    public List<DmValue> Values { get; } = [];
    public Dictionary<DmValue, DmValue> Associations { get; }
    public DmList() { Associations = []; }
    internal DmList(IEqualityComparer<DmValue> comparer) { Associations = new(comparer); }
    public DmValue Get(DmValue index) {
        if (index.Value is float n) {
            return Values[NumericIndex(n)];
        }
        return Associations.GetValueOrDefault(index);
    }
    private int NumericIndex(float number) {
        var index = Math.Truncate((double)number);
        if (!double.IsFinite(index) || index < 1 || index > Values.Count)
            throw new CompatibilityException("DM list index out of bounds.");
        return (int)index - 1;
    }
    public void Set(DmValue index, DmValue value) {
        if (index.Value is float n) {
            Values[NumericIndex(n)] = value;
        } else {
            if (!Values.Contains(index)) Values.Add(index);
            Associations[index] = value;
        }
    }
}
/// <summary>Strict associative lists have exact keys, including null and numbers.</summary>
public sealed class DmAssocList {
    private readonly Dictionary<DmValue, DmValue> _pairs = [];
    public int Count => _pairs.Count;
    public bool ContainsKey(DmValue key) => _pairs.ContainsKey(key);
    public DmValue Get(DmValue key) => _pairs.GetValueOrDefault(key);
    public void Set(DmValue key, DmValue value) => _pairs[key] = value;
}
public sealed record WorldTile(int X, int Y, int Z, bool Density, string Type, string? Color = null);
public sealed record MapEntity(int X, int Y, int Z, string Type);
public sealed record RuntimeLog(double Time, string Text);

/// <summary>Retained static map objects; diagnostic records remain separate snapshots.</summary>
public sealed record MapCellObjects(int X, int Y, int Z, DmObject Turf, DmObject Area, IReadOnlyList<DmObject> Entities);
