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
        DmTypeReference t => t.Path, DmResource r => r.Path, DmObject o => o.Variables.TryGetValue("name", out var n) && !n.IsNull ? n.ToString() : o.TypePath,
        DmList => throw new CompatibilityException("List stringification is not implemented."),
        _ => throw new CompatibilityException($"Unsupported DM value {Value.GetType().Name}.")
    };
    public static implicit operator DmValue(float value) => new(value);
    public static implicit operator DmValue(int value) => new((float)value);
    public static implicit operator DmValue(string value) => new(value);
}
public sealed record DmTypeReference(int Id, string Path);
public sealed record DmResource(string Path);
public sealed class DmObject(int typeId, string typePath) {
    public int TypeId { get; } = typeId;
    public string TypePath { get; } = typePath;
    public Dictionary<string, DmValue> Variables { get; } = new(StringComparer.Ordinal);
}
/// <summary>Ordered list entries plus associative keys, distinct from a host dictionary.</summary>
public sealed class DmList {
    public List<DmValue> Values { get; } = [];
    public Dictionary<DmValue, DmValue> Associations { get; } = [];
    public DmValue Get(DmValue index) {
        if (index.Value is float n) {
            if (n != MathF.Truncate(n) || n < 1 || n > Values.Count) throw new CompatibilityException("DM list index out of bounds.");
            return Values[(int)n - 1];
        }
        return Associations.GetValueOrDefault(index);
    }
    public void Set(DmValue index, DmValue value) {
        if (index.Value is float n) {
            if (n != MathF.Truncate(n) || n < 1 || n > Values.Count) throw new CompatibilityException("DM list index out of bounds.");
            Values[(int)n - 1] = value;
        } else {
            if (!Values.Contains(index)) Values.Add(index);
            Associations[index] = value;
        }
    }
}
public sealed record WorldTile(int X, int Y, int Z, bool Density, string Type, string? Color = null);
public sealed record MapEntity(int X, int Y, int Z, string Type);
public sealed record RuntimeLog(double Time, string Text);
