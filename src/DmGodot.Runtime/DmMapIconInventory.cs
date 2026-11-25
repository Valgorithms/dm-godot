namespace DmGodot.Runtime;

/// <summary>A host-approved association between a compiler resource token and a tracked icon asset.</summary>
public sealed record DmMapIconBinding(int ResourceIndex, string ResourceToken, string InventoryPath);

/// <summary>Immutable, names-only capability for literal map icons in one compiled project.</summary>
public sealed class DmMapIconInventory {
    public const int MaximumBindings = 256;
    public const int MaximumNameUnits = 1_048_576;
    private const int ConstructionWork = 4_194_304;
    private sealed record ApprovedIcon(DmMapIconBinding Binding, DmResource Resource);
    private readonly ApprovedIcon[] _bindings;
    internal CompiledProject Project { get; }

    public DmMapIconInventory(CompiledProject project, DmFileInventory assets, IEnumerable<DmMapIconBinding> bindings) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(bindings);
        var entries = new List<ApprovedIcon>();
        var remaining = ConstructionWork;
        var names = 0;
        void Work(int cost) {
            if (cost > remaining) throw new CompatibilityException("Map icon inventory construction work limit exceeded.");
            remaining -= cost;
        }

        foreach (var binding in bindings) {
            if (entries.Count == MaximumBindings) throw new CompatibilityException("Map icon inventory binding count limit exceeded.");
            if (binding is null || string.IsNullOrEmpty(binding.ResourceToken) || string.IsNullOrEmpty(binding.InventoryPath) ||
                binding.ResourceToken.Length > DmFileInventory.MaximumPathLength || binding.InventoryPath.Length > DmFileInventory.MaximumPathLength)
                throw new CompatibilityException("Invalid map icon inventory name length.");
            var token = binding.ResourceToken;
            var path = binding.InventoryPath;
            names += token.Length + path.Length;
            if (names > MaximumNameUnits) throw new CompatibilityException("Map icon inventory name limit exceeded.");
            Work(1 + token.Length * 2 + path.Length * 2);
            // The compiler token is an opaque identity and may contain relocation components.
            // Only the independently supplied canonical path is an inventory query.
            if (token.Any(char.IsControl) || !token.EndsWith(".dmi", StringComparison.Ordinal) ||
                !path.EndsWith(".dmi", StringComparison.Ordinal))
                throw new CompatibilityException("Map icon inventory requires literal .dmi resources.");
            var resources = project.Data.Resources ?? throw new CompatibilityException("Map icon inventory requires an available compiler resource table.");
            if ((uint)binding.ResourceIndex >= (uint)resources.Length ||
                !string.Equals(resources[binding.ResourceIndex], token, StringComparison.Ordinal))
                throw new CompatibilityException("Map icon inventory compiler resource index/token mismatch.");
            DmFileInventory.ValidatePath(path, allowDirectory: false);
            if (!assets.ContainsRegularFile(path, Work))
                throw new CompatibilityException("Map icon inventory requires a tracked regular asset file.");
            foreach (var previous in entries) {
                Work(1 + Math.Min(previous.Binding.InventoryPath.Length, path.Length));
                if (previous.Binding.InventoryPath == path) throw new CompatibilityException("Duplicate map icon inventory path.");
            }
            var index = Search(entries, token, Work);
            if (index >= 0) throw new CompatibilityException("Duplicate map icon inventory token.");
            Work(3 + entries.Count); // Immutable literal, entry, and bounded insertion work.
            entries.Insert(~index, new(binding, new DmResource(token)));
        }
        Project = project;
        _bindings = entries.ToArray();
    }

    private static int Search(IReadOnlyList<ApprovedIcon> entries, string token, Action<int> work) {
        var low = 0;
        var high = entries.Count - 1;
        while (low <= high) {
            var middle = low + (high - low) / 2;
            var candidate = entries[middle].Binding.ResourceToken;
            work(1 + Math.Min(candidate.Length, token.Length));
            var order = string.CompareOrdinal(candidate, token);
            if (order == 0) return middle;
            if (order < 0) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    private ApprovedIcon? Find(string token, Action<int> work) {
        if (string.IsNullOrEmpty(token) || token.Length > DmFileInventory.MaximumPathLength)
            return null;
        var index = Search(_bindings, token, work);
        return index < 0 ? null : _bindings[index];
    }

    internal bool ContainsToken(string token, Action<int> work) => Find(token, work) is not null;

    internal DmResource Resolve(string token, Action<int> work) {
        var binding = Find(token, work) ?? throw new CompatibilityException("Map icon resource has no approved inventory binding.");
        return binding.Resource;
    }
}
