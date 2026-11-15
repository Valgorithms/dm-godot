using System.Text;

namespace DmGodot.Runtime;

/// <summary>Owned immutable text from an explicit allowlisted source snapshot; never host I/O.</summary>
public sealed class DmTextFileSnapshot {
    public const int MaximumFiles = 256;
    public const int MaximumFileBytes = 64 * 1024;
    public const int MaximumTotalBytes = 1024 * 1024;
    private readonly DmFileInventory _inventory;
    internal DmFileInventory Inventory => _inventory;
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    public DmTextFileSnapshot(DmFileInventory inventory, IEnumerable<KeyValuePair<string, byte[]>> contents) {
        _inventory = inventory;
        long rawTotal = 0, decodedTotal = 0;
        var count = 0;
        foreach (var (path, bytes) in contents) {
            if (++count > MaximumFiles) throw new CompatibilityException("Text snapshot file count limit exceeded.");
            DmFileInventory.ValidatePath(path, allowDirectory: false);
            if (!_inventory.ContainsFile(path)) throw new CompatibilityException("Text snapshot content is not a regular inventory file.");
            if (bytes is null || bytes.Length > MaximumFileBytes)
                throw new CompatibilityException("Text snapshot per-file byte limit exceeded.");
            rawTotal += bytes.Length;
            if (rawTotal > MaximumTotalBytes) throw new CompatibilityException("Text snapshot total raw byte limit exceeded.");
            // Pinned DreamResource.ReadAsString uses UTF-8 replacement decoding and
            // only collapses CRLF. BOM, lone CR and embedded NUL remain observable.
            var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
            decodedTotal += (long)text.Length * sizeof(char);
            if (decodedTotal > MaximumTotalBytes) throw new CompatibilityException("Text snapshot total decoded byte limit exceeded.");
            if (!_text.TryAdd(path, text)) throw new CompatibilityException("Duplicate text snapshot path.");
        }
    }

    public string? ReadText(string path) {
        if (!_inventory.ContainsFile(path)) return null;
        if (!_text.TryGetValue(path, out var text))
            throw new CompatibilityException("file2text content capability excludes this tracked file.");
        return text;
    }
}
