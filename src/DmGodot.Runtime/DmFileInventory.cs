using System.Text;

namespace DmGodot.Runtime;

/// <summary>
/// Immutable, explicitly supplied tracked-source filesystem snapshot. This class never
/// queries the host filesystem. It does not represent live files or persistent state.
/// </summary>
public sealed class DmFileInventory {
    public const int MaximumPaths = 100_000;
    public const int MaximumPathLength = 4096;
    public const int MaximumUtf8Bytes = 16 * 1024 * 1024;
    public const int MaximumPrefixWork = 32 * 1024 * 1024;
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);

    public DmFileInventory(IEnumerable<string> files, IEnumerable<string>? blockedPaths = null) {
        var count = 0;
        long bytes = 0;
        void Add(IEnumerable<string> paths, HashSet<string> destination) {
            foreach (var path in paths) {
                if (++count > MaximumPaths) throw new CompatibilityException("File inventory path count limit exceeded.");
                ValidatePath(path, allowDirectory: false);
                if (path.Length == 0) throw new CompatibilityException("Empty file inventory entry.");
                bytes += Encoding.UTF8.GetByteCount(path);
                if (bytes > MaximumUtf8Bytes) throw new CompatibilityException("File inventory byte limit exceeded.");
                if (!destination.Add(path)) throw new CompatibilityException("Duplicate file inventory entry.");
            }
        }
        Add(files, _files);
        Add(blockedPaths ?? [], _blocked);
        long prefixWork = 0;
        foreach (var path in _files) {
            if (_blocked.Count == 0) break;
            prefixWork += path.Length;
            for (var i = 0; i < path.Length; i++)
                if (path[i] == '/') prefixWork += i;
            if (prefixWork > MaximumPrefixWork)
                throw new CompatibilityException("File inventory prefix work limit exceeded.");
            if (IsBlocked(path)) throw new CompatibilityException("Regular file overlaps a blocked inventory path.");
        }
    }

    // Exact ordinal relative names only. No normalization, aliases, host cwd, drive,
    // resource cache, symlink resolution or OS-dependent separator behavior.
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

    private bool IsBlocked(string path) {
        if (_blocked.Count == 0) return false;
        if (_blocked.Contains(path)) return true;
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '/' && _blocked.Contains(path[..i])) return true;
        return false;
    }

    public bool ContainsFile(string path) {
        ValidatePath(path);
        if (IsBlocked(path))
            throw new CompatibilityException("File snapshot query targets an unsupported symlink or submodule path.");
        return path.Length != 0 && !path.EndsWith('/') && _files.Contains(path);
    }
}
