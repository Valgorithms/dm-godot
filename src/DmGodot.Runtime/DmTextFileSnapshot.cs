using System.Text;

namespace DmGodot.Runtime;

/// <summary>Owned immutable text from an explicit allowlist; never host I/O.</summary>
public sealed class DmTextFileSnapshot {
    public const int MaximumFiles = 256;
    public const int MaximumFileBytes = 64 * 1024;
    public const int MaximumTotalBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal DmFileInventory Inventory { get; }
    private readonly string[] _paths;
    private readonly string[] _texts;

    public DmTextFileSnapshot(DmFileInventory inventory, IEnumerable<KeyValuePair<string, byte[]>> contents) {
        Inventory = inventory;
        var paths = new List<string>(); var texts = new List<string>();
        long rawTotal = 0, decodedTotal = 0;
        var remainingWork = DmFileInventory.MaximumPrefixWork;
        void Work(int amount) {
            if (amount < 0 || amount > remainingWork)
                throw new CompatibilityException("Text snapshot construction work limit exceeded.");
            remainingWork -= amount;
        }
        foreach (var (path, bytes) in contents) {
            if (paths.Count >= MaximumFiles) throw new CompatibilityException("Text snapshot file count limit exceeded.");
            if (path is null || path.Length > DmFileInventory.MaximumPathLength)
                throw new CompatibilityException("Text snapshot path length limit exceeded.");
            Work(1 + path.Length * 2);
            DmFileInventory.ValidatePath(path, allowDirectory: false);
            if (!inventory.ContainsRegularFile(path, Work))
                throw new CompatibilityException("Text snapshot content is not a regular inventory file.");
            if (bytes is null || bytes.Length > MaximumFileBytes)
                throw new CompatibilityException("Text snapshot per-file byte limit exceeded.");
            rawTotal += bytes.Length;
            if (rawTotal > MaximumTotalBytes) throw new CompatibilityException("Text snapshot total raw byte limit exceeded.");
            Work(bytes.Length);
            var text = Decode(bytes);
            decodedTotal += (long)text.Length * sizeof(char);
            if (decodedTotal > MaximumTotalBytes) throw new CompatibilityException("Text snapshot total decoded byte limit exceeded.");
            paths.Add(path); texts.Add(text);
        }
        _paths = paths.ToArray(); _texts = texts.ToArray();
        // At most 256 names of 4096 code units: deterministic ordinal comparison,
        // bounded construction input, no user comparer/hash or runtime callbacks retained.
        Array.Sort(_paths, _texts, StringComparer.Ordinal);
        for (var i = 1; i < _paths.Length; i++)
            if (string.Equals(_paths[i - 1], _paths[i], StringComparison.Ordinal))
                throw new CompatibilityException("Duplicate text snapshot path.");
    }

    private static string Decode(ReadOnlySpan<byte> bytes) {
        var zero = bytes.IndexOf((byte)0);
        if (zero >= 0) bytes = bytes[..zero];
        var bom = bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        if (bom) bytes = bytes[3..];
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) {
            // Measured isolated high-byte subset; not a general malformed-UTF8 fallback.
            if (bom) throw new CompatibilityException("Text snapshot encoding is not supported.");
            for (var i = 0; i < bytes.Length; i++) {
                if (bytes[i] < 128) continue;
                if (!IsSupportedLatin1Byte(bytes, i))
                    throw new CompatibilityException("Text snapshot encoding is not supported.");
            }
            text = Encoding.Latin1.GetString(bytes);
        }
        return text.Replace("\r", "", StringComparison.Ordinal);
    }
    // BYOND 516.1688 maps 0x80..0xFE before LF to Latin-1. Its 0xFF result differs.
    private static bool IsSupportedLatin1Byte(ReadOnlySpan<byte> bytes, int i) =>
        i > 0 && i + 1 < bytes.Length && AsciiLetter(bytes[i - 1]) &&
        (AsciiLetter(bytes[i + 1]) || (bytes[i] != 0xff && bytes[i + 1] == (byte)'\n'));
    private static bool AsciiLetter(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    // Strict UTF-8 and the supported Latin-1 fallback both produce well-formed
    // UTF-16. Removing CR cannot split a surrogate pair. Only an exact reference
    // can reuse that guarantee without inspecting the caller's characters.
    internal bool TryConfirmDecodedText(string text, Action<int> work) {
        if (_texts.Length == 0 || text.Length <= _texts.Length) return false;
        work(_texts.Length); // Fixed upper bound, independent of the matching slot.
        foreach (var decoded in _texts)
            if (ReferenceEquals(decoded, text)) return true;
        return false;
    }

    public string? ReadText(string path) => ReadText(path, static _ => { });

    internal string? ReadText(string path, Action<int> work) {
        if (!Inventory.ContainsPath(path, work)) return null;
        if (!Inventory.ContainsRegularFile(path, work)) return string.Empty;
        var low = 0; var high = _paths.Length;
        while (low < high) {
            var middle = low + (high - low) / 2;
            work(1 + Math.Min(_paths[middle].Length, path.Length));
            var order = string.CompareOrdinal(_paths[middle], path);
            if (order == 0) return _texts[middle];
            if (order < 0) low = middle + 1; else high = middle;
        }
        throw new CompatibilityException("file2text content capability excludes this tracked file.");
    }
}
