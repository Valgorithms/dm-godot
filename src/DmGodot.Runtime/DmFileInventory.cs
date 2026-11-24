using System.Text;

namespace DmGodot.Runtime;

/// <summary>
/// Immutable explicit tracked-source names. Never queries the host filesystem;
/// implicit directories describe this snapshot, not live or persistent state.
/// </summary>
public sealed class DmFileInventory {
    public const int MaximumPaths = 100_000;
    public const int MaximumPathLength = 4096;
    public const int MaximumUtf8Bytes = 16 * 1024 * 1024;
    public const int MaximumPrefixWork = 32 * 1024 * 1024;
    private readonly string[] _files;
    private readonly string[] _blocked;
    private readonly string[] _entries;

    public DmFileInventory(IEnumerable<string> files, IEnumerable<string>? blockedPaths = null) {
        var count = 0;
        long bytes = 0;
        string[] Copy(IEnumerable<string> paths) {
            var result = new List<string>();
            foreach (var path in paths) {
                if (++count > MaximumPaths) throw new CompatibilityException("File inventory path count limit exceeded.");
                ValidatePath(path, allowDirectory: false);
                if (path.Length == 0) throw new CompatibilityException("Empty file inventory entry.");
                bytes += Encoding.UTF8.GetByteCount(path);
                if (bytes > MaximumUtf8Bytes) throw new CompatibilityException("File inventory byte limit exceeded.");
                result.Add(path);
            }
            return result.ToArray();
        }
        _files = Copy(files);
        _blocked = Copy(blockedPaths ?? []);
        _entries = new string[count];
        _files.CopyTo(_entries, 0);
        _blocked.CopyTo(_entries, _files.Length);
        // Bound construction comparisons too. No hashing, user comparers or host I/O.
        long remaining = MaximumPrefixWork;
        void Work(int amount) {
            remaining -= amount;
            if (remaining < 0) throw new CompatibilityException("File inventory prefix work limit exceeded.");
        }
        var comparer = Comparer<string>.Create((a, b) => {
            Work(1 + Math.Min(a.Length, b.Length));
            return string.CompareOrdinal(a, b);
        });
        try {
            Array.Sort(_files, comparer);
            Array.Sort(_blocked, comparer);
            Array.Sort(_entries, comparer);
        } catch (InvalidOperationException e) when (e.InnerException is CompatibilityException inner) {
            throw inner;
        }
        for (var i = 0; i < _entries.Length; i++) {
            var path = _entries[i];
            if (i > 0) {
                Work(1 + Math.Min(path.Length, _entries[i - 1].Length));
                if (string.Equals(path, _entries[i - 1], StringComparison.Ordinal))
                    throw new CompatibilityException("Duplicate file inventory entry.");
            }
            // Any stored ancestor denotes a file or blocked link, not a directory.
            for (var end = 0; end < path.Length; end++) {
                Work(1);
                if (path[end] == '/' && Contains(_entries, path.AsSpan(0, end), Work))
                    throw new CompatibilityException("Contradictory file inventory path hierarchy.");
            }
        }
    }

    public static void ValidatePath(string path, bool allowDirectory = true) {
        if (path is null || path.Length > MaximumPathLength)
            throw new CompatibilityException("File snapshot path length limit exceeded.");
        if (path.Any(c => char.IsControl(c) || c is '\\' or ':' or '*' or '?'))
            throw new CompatibilityException("Unsafe or unsupported file snapshot path.");
        if (path.Length == 0) return;
        var end = path.EndsWith('/') && allowDirectory ? path.Length - 1 : path.Length;
        if (end == 0) throw new CompatibilityException("Absolute file snapshot path is not permitted.");
        var start = 0;
        for (var i = 0; i <= end; i++) {
            if (i != end && path[i] != '/') continue;
            var component = path.AsSpan(start, i - start);
            if (component.Length == 0 || component.SequenceEqual(".") || component.SequenceEqual("..") ||
                component.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new CompatibilityException("Unsafe file snapshot path component.");
            start = i + 1;
        }
    }

    private static int LowerBound(string[] entries, ReadOnlySpan<char> query, Action<int> work) {
        var low = 0; var high = entries.Length;
        while (low < high) {
            var middle = low + (high - low) / 2;
            work(1 + Math.Min(entries[middle].Length, query.Length));
            if (entries[middle].AsSpan().SequenceCompareTo(query) < 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static bool Contains(string[] entries, ReadOnlySpan<char> query, Action<int> work) {
        var index = LowerBound(entries, query, work);
        if (index == entries.Length) return false;
        work(1 + Math.Min(entries[index].Length, query.Length));
        return entries[index].AsSpan().SequenceEqual(query);
    }

    public bool ContainsFile(string path) => ContainsPath(path, static _ => { });

    // Callers validate paths first; directories/root must never authorize content.
    internal bool ContainsRegularFile(string path, Action<int> work) => Contains(_files, path, work);

    internal bool ContainsPath(string path, Action<int> work) {
        if (path is null || path.Length > MaximumPathLength)
            throw new CompatibilityException("File snapshot path length limit exceeded.");
        work(1 + path.Length * 2); // Character validation and component traversal.
        ValidatePath(path);
        if (path.Length == 0) return true; // Explicitly configured logical root.
        var trailing = path.EndsWith('/');
        var length = trailing ? path.Length - 1 : path.Length;
        var name = path.AsSpan(0, length);
        if (Contains(_blocked, name, work))
            throw new CompatibilityException("File snapshot query targets an unsupported symlink or submodule path.");
        for (var i = 0; i < length; i++) {
            work(1);
            if (path[i] == '/' && Contains(_blocked, path.AsSpan(0, i), work))
                throw new CompatibilityException("File snapshot query targets an unsupported symlink or submodule path.");
        }
        if (!trailing && Contains(_files, name, work)) return true;
        work(length + 1); // Charge the sole bounded prefix allocation before copying.
        var prefix = string.Concat(name, "/");
        var next = LowerBound(_entries, prefix, work);
        if (next == _entries.Length) return false;
        work(1 + prefix.Length);
        return _entries[next].StartsWith(prefix, StringComparison.Ordinal);
    }
}
