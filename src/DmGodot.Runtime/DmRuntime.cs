using System.Text;
using System.Text.Json;
using DMCompiler.Bytecode;

namespace DmGodot.Runtime;

/// <summary>
/// Experimental single-threaded DM execution core. Time is virtual deciseconds.
/// Unsupported operations fail at their DM source location; they are never silently skipped.
/// </summary>
public sealed partial class DmRuntime {
    public CompiledProject Project { get; }
    public double Time { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Levels { get; private set; }
    public IReadOnlyList<WorldTile> Tiles => _tiles;
    public IReadOnlyList<MapEntity> MapEntities => _entities;
    public IReadOnlyList<MapCellObjects> MapCells => _mapCells.AsReadOnly();
    public IReadOnlyList<RuntimeLog> Logs => _logs;
    public int PendingThreads => _queue.Count;
    public DmObject World { get; }
    private readonly List<WorldTile> _tiles = [];
    private readonly List<MapEntity> _entities = [];
    private readonly List<MapCellObjects> _mapCells = [];
    private readonly Dictionary<(int X, int Y, int Z), DmObject> _mapTurfs = [];
    private readonly Dictionary<DmObject, MapLocation> _mapLocations = [];
    private readonly HashSet<DmObject> _multiLevelAreas = [];
    private readonly List<DmObject> _mapMovableNewOrder = [];
    private DmObject? _defaultArea;
    private sealed record MapLocation(int X, int Y, int Z, DmObject? Loc, bool NameOverridden = false,
        bool InvisibilityOverridden = false, MapEntity? MovableSnapshot = null);
    // Native fields require appearance, containment or connection adapters, not raw compiler defaults.
    // Names are pinned to DMStandard/Types/Atoms at the supported compiler revision.
    private static readonly HashSet<string> UnsupportedAtomFields = new(StringComparer.Ordinal) {
        "name", "text", "desc", "suffix", "verbs", "contents", "overlays", "underlays", "vis_locs", "vis_contents",
        "dir", "pixel_x", "pixel_y", "pixel_z", "pixel_w", "icon_w", "icon_z", "icon", "icon_state", "layer", "plane",
        "alpha", "color", "invisibility", "mouse_opacity", "infra_luminosity", "luminosity", "opacity", "transform",
        "blend_mode", "gender", "density", "maptext", "filters", "appearance", "appearance_flags",
        "maptext_width", "maptext_height", "maptext_x", "maptext_y", "step_x", "step_y", "render_source",
        "mouse_drag_pointer", "mouse_drop_pointer", "mouse_over_pointer", "mouse_drop_zone", "render_target", "vis_flags", "pixloc"
    };
    private static readonly HashSet<string> UnsupportedMovableFields = new(StringComparer.Ordinal) {
        "screen_loc", "animate_movement", "locs", "glide_size", "step_size", "bound_x", "bound_y", "bound_width", "bound_height", "bounds", "particles"
    };
    private static readonly HashSet<string> UnsupportedMobFields = new(StringComparer.Ordinal) {
        "client", "key", "ckey", "group", "see_invisible", "see_infrared", "sight", "see_in_dark"
    };
    private bool UnsupportedSubtypeField(DmObject obj, string name) =>
        (IsSubtypeOf(obj.TypeId, "/atom/movable") && UnsupportedMovableFields.Contains(name)) ||
        (IsSubtypeOf(obj.TypeId, "/mob") && UnsupportedMobFields.Contains(name));
    private bool UnsupportedMapField(DmObject obj, string name) =>
        UnsupportedAtomFields.Contains(name) || UnsupportedSubtypeField(obj, name);
    private static bool ProtectedAtomField(string name) =>
        UnsupportedAtomFields.Contains(name) || name is "loc" or "x" or "y" or "z";
    private bool ProtectedMapField(DmObject obj, string name) =>
        ProtectedAtomField(name) || UnsupportedSubtypeField(obj, name);
    private readonly List<RuntimeLog> _logs = [];
    private readonly PriorityQueue<Fiber, (double Time, long Sequence)> _queue = new();
    private readonly DmValue[] _globals;
    private readonly Dictionary<int, TypeDefaults> _typeDefaults = [];
    private sealed record TypeDefaults(IReadOnlyDictionary<string, DmValue> Variables,
        DmValue Type, DmValue ParentType, string? ExecutableInitializerType);
    private long _sequence;
    private bool _booted;
    private Fiber? _parkedGlobalInitializer;
    private CancellationToken _startupCancellation;
    private int _budget;
    internal DmGlobalWork? LastGlobalWork { get; private set; }
    // Bound constructor work as well as bytecode dispatch: one dimension opcode can
    // otherwise allocate an unbounded graph. Reset only at public execution boundaries.
    private const int ListAllocationLimit = 100_000;
    private int _listAllocationRemaining = ListAllocationLimit;
    private const int SplitTextOutputByteLimit = 1024 * 1024;
    private int _splitTextOutputBytesRemaining = SplitTextOutputByteLimit;
    private int _immediateSpawnDepth;
    // Built-ins from the pinned OpenDream World.dm. User-defined vars remain ordinary fields.
    private static readonly HashSet<string> BuiltinWorldFields = new(StringComparer.Ordinal) {
        "contents", "vars", "log", "area", "turf", "mob", "name", "time", "timezone", "timeofday", "realtime",
        "tick_lag", "cpu", "fps", "tick_usage", "loop_checks", "maxx", "maxy", "maxz", "icon_size", "view",
        "movement_mode", "byond_version", "byond_build", "version", "address", "port", "internet_address", "url",
        "visibility", "status", "process", "params", "sleep_offline", "system_type", "map_cpu", "hub", "hub_password",
        "reachable", "game_state", "host", "map_format", "cache_lifespan", "executor", "opendream_topic_port"
    };
    private static readonly HashSet<string> ReadableWorldFields = new(StringComparer.Ordinal) {
        "time", "maxx", "maxy", "maxz", "name", "area", "turf", "mob", "icon_size", "view", "byond_version", "byond_build",
        "fps", "tick_lag"
    };
    public const int DefaultInstructionBudget = 100_000;
    public int InstructionBudget { get; init; } = DefaultInstructionBudget;
    private CompiledData Data => Project.Data;

    private readonly DmFileInventory? _fileInventory;
    private readonly DmTextFileSnapshot? _textFiles;
    private readonly DmMapIconInventory? _mapIcons;

    // Retain the existing constructor for hosts built against the three-argument signature.
    public DmRuntime(CompiledProject project, DmFileInventory? fileInventory, DmTextFileSnapshot? textFiles)
        : this(project, fileInventory, textFiles, default, null) { }

    public DmRuntime(CompiledProject project, DmFileInventory? fileInventory = null, DmTextFileSnapshot? textFiles = null,
        DmMapIconInventory? mapIcons = null)
        : this(project, fileInventory, textFiles, default, mapIcons) { }

    internal DmRuntime(CompiledProject project, DmFileInventory? fileInventory, DmTextFileSnapshot? textFiles,
        CancellationToken startupCancellation, DmMapIconInventory? mapIcons) {
        _startupCancellation = startupCancellation;
        _startupCancellation.ThrowIfCancellationRequested();
        Project = project;
        if (mapIcons is not null && !ReferenceEquals(project, mapIcons.Project))
            throw new CompatibilityException("Map icon inventory must be bound to the same compiled project.");
        _mapIcons = mapIcons;
        _fileInventory = fileInventory;
        if (textFiles is not null && !ReferenceEquals(fileInventory, textFiles.Inventory))
            throw new CompatibilityException("Text snapshot must share the configured file inventory identity.");
        _textFiles = textFiles;
        _globals = new DmValue[Data.Globals?.GlobalCount ?? 0];
        foreach (var (id, value) in Data.Globals?.GlobalValues ?? []) {
            _startupCancellation.ThrowIfCancellationRequested();
            if (id < 0 || id >= _globals.Length) throw new CompatibilityException($"Invalid global index {id}.");
            _globals[id] = ConvertValue(value);
        }
        World = CreateInstance(FindType("/world"));
        // Constructor-time initialization has one fixed aggregate budget; the init-only
        // InstructionBudget setting applies to later Boot/InvokeGlobal/AdvanceBy calls.
        _budget = DefaultInstructionBudget;
        LoadMaps();
        World.SetVariable("maxx", Width);
        World.SetVariable("maxy", Height);
        World.SetVariable("maxz", Levels);
        _startupCancellation.ThrowIfCancellationRequested();
    }

    public void Boot() {
        AdvanceGlobalInitialization(long.MaxValue);
        FinishBoot();
    }

    internal bool AdvanceGlobalInitialization(long maxDispatches,
        DmGlobalInitializationProfile profile = DmGlobalInitializationProfile.Standard) {
        _startupCancellation.ThrowIfCancellationRequested();
        var fiber = _parkedGlobalInitializer;
        _parkedGlobalInitializer = null;
        if (fiber is null) {
            if (_booted) throw new InvalidOperationException("The world has already booted.");
            _booted = true;
            _budget = profile == DmGlobalInitializationProfile.Extended ? (int)profile : InstructionBudget;
            _listAllocationRemaining = ListAllocationLimit;
            _splitTextOutputBytesRemaining = SplitTextOutputByteLimit;
        }
        var startingWork = Math.Max(0, _budget);
        try {
            if (fiber is null && Data.GlobalInitProc is { } init) fiber = new Fiber(new Frame(init, null, []));
            if (fiber is not null) {
                if (!ExecuteGlobalDispatches(fiber, maxDispatches)) {
                    _parkedGlobalInitializer = fiber;
                    return false;
                }
                if (fiber.Frames.Count > 0) throw new CompatibilityException("A yielding global initializer is not supported.");
            }
            _startupCancellation.ThrowIfCancellationRequested();
            return true;
        } finally {
            // The dispatch guard decrements before rejecting an exhausted budget.
            var remaining = Math.Clamp(_budget, 0, startingWork);
            LastGlobalWork = new DmGlobalWork(startingWork - remaining, remaining);
        }
    }

    private bool ExecuteGlobalDispatches(Fiber fiber, long maxDispatches) {
        // Each native runs inside its calling dispatch. While the cumulative balance is larger
        // than the ordinary allowance, lend one dispatch at most that allowance.
        while (_budget > InstructionBudget && maxDispatches > 0) {
            var reserve = _budget - InstructionBudget;
            _budget = InstructionBudget;
            long oneDispatch = 1;
            try {
                if (ExecuteDispatches(fiber, ref oneDispatch)) return true;
            } finally {
                _budget = reserve + Math.Max(0, _budget);
            }
            maxDispatches--;
        }
        return ExecuteDispatches(fiber, ref maxDispatches);
    }

    internal void FinishBoot() {
        _startupCancellation.ThrowIfCancellationRequested();
        // Only globals can spend the extended allowance. Callbacks share at most one ordinary budget.
        _budget = Math.Min(_budget, InstructionBudget);
        // Each callback is an independent root: execute its prefix now, but do not
        // drain deferred work until all map callbacks and world.New have started.
        var visitedAreas = new HashSet<DmObject>();
        foreach (var cell in _mapCells) {
            _startupCancellation.ThrowIfCancellationRequested();
            if (visitedAreas.Add(cell.Area)) StartNew(cell.Area, []);
        }
        if (_defaultArea is not null && visitedAreas.Add(_defaultArea)) StartNew(_defaultArea, []);
        foreach (var cell in _mapCells.OrderBy(c => c.Z).ThenByDescending(c => c.Y).ThenByDescending(c => c.X))
            StartNew(cell.Turf, []);
        foreach (var movable in _mapMovableNewOrder) {
            // Upstream creates each movable here, after turf New prefixes. Its <init>
            // precedes native loc assignment and New. Our retained loc is already set,
            // but the validated initializer cannot observe it (or any other field).
            if (ResolveInitializer(movable.TypeId) is { } initializer)
                Execute(new Fiber(new Frame(initializer, movable, [])));
            StartNew(movable, [new DmValue(_mapLocations[movable].Loc)]);
        }
        StartNew(World, []);
        // Preserve the existing deferred-work instruction slice, but keep list
        // allocation charged to the same public Boot boundary.
        _budget = InstructionBudget;
        DrainUntil(Time);
        // The session may now transfer the world. Its token has no authority over play.
        _startupCancellation.ThrowIfCancellationRequested();
        _startupCancellation = default;
    }

    private void StartNew(DmObject instance, DmValue[] arguments) {
        _startupCancellation.ThrowIfCancellationRequested();
        var proc = ResolveProc(instance.TypeId, "New");
        if (proc is not null) Execute(new Fiber(new Frame(proc, instance, arguments)));
    }

    public void AdvanceBy(double deciseconds) {
        if (!_booted) throw new InvalidOperationException("Boot the runtime before advancing it.");
        if (!double.IsFinite(deciseconds) || deciseconds < 0) throw new ArgumentOutOfRangeException(nameof(deciseconds));
        var target = Time + deciseconds;
        if (!double.IsFinite(target)) throw new ArgumentOutOfRangeException(nameof(deciseconds));
        _budget = InstructionBudget;
        _listAllocationRemaining = ListAllocationLimit;
        _splitTextOutputBytesRemaining = SplitTextOutputByteLimit;
        DrainUntil(target);
    }

    private void DrainUntil(double target) {
        while (_queue.TryPeek(out _, out var priority) && priority.Time <= target) {
            Time = priority.Time;
            Execute(_queue.Dequeue());
        }
        Time = target;
    }

    /// <summary>Start a global proc at the current time, retaining DM call/sleep semantics.</summary>
    public void InvokeGlobal(string name, params DmValue[] arguments) {
        if (!_booted) throw new InvalidOperationException("Boot the runtime first.");
        var id = (Data.GlobalProcs ?? []).FirstOrDefault(i => Data.Procs[i].Name == name, -1);
        if (id < 0) throw new CompatibilityException($"Unknown global proc '{name}'.");
        if (Data.Procs[id].Bytecode is not { Length: > 0 })
            throw new CompatibilityException($"Direct invocation of an empty/native proc '{name}' is not implemented.");
        Enqueue(new Fiber(new Frame(Data.Procs[id], null, arguments)), Time);
        AdvanceBy(0);
    }

    public DmValue GetGlobal(string name) {
        var index = Array.IndexOf(Data.Globals?.Names ?? [], name);
        return index >= 0 && index < _globals.Length ? _globals[index] : throw new KeyNotFoundException(name);
    }

    private void Enqueue(Fiber fiber, double when) => _queue.Enqueue(fiber, (when, _sequence++));
    private int FindType(string path) {
        var id = Array.FindIndex(Data.Types, t => t.Path == path);
        return id >= 0 ? id : throw new CompatibilityException($"Required type {path} is absent.");
    }
    private DmObject CreateInstance(int id, bool initializeLater = false) {
        if (id < 0 || id >= Data.Types.Length) throw new CompatibilityException($"Invalid type ID {id}.");
        var defaults = GetTypeDefaults(id, initializeLater);
        return new DmObject(id, Data.Types[id].Path, defaults.Variables, defaults.Type, defaults.ParentType);
    }

    private TypeDefaults GetTypeDefaults(int id, bool initializeLater, bool mappedTurf = false) {
        // Walk iteratively to the nearest complete prototype, preserving cold-path
        // ancestor guard/conversion ordering. Never cache a partly converted type.
        var pending = new Stack<int>();
        TypeDefaults? inherited = null;
        for (int? current = id; current is not null; current = Data.Types[current.Value].Parent) {
            if (_typeDefaults.TryGetValue(current.Value, out inherited)) break;
            pending.Push(current.Value);
        }
        if (!initializeLater && inherited?.ExecutableInitializerType is { } cachedInitializer)
            throw new CompatibilityException($"Type initializers are not implemented: {cachedInitializer}.");
        bool checkedTurfDepth = false;
        while (pending.TryPop(out var typeId)) {
            var type = Data.Types[typeId];
            var initializer = type.InitProc is int init && Data.Procs[init].Bytecode is { Length: > 0 }
                ? type.Path : null;
            if (!initializeLater && initializer is not null)
                throw new CompatibilityException($"Type initializers are not implemented: {initializer}.");
            bool newTurfPrototype = mappedTurf && (initializer is not null || inherited?.ExecutableInitializerType is not null);
            if (newTurfPrototype) {
                // The first formerly blocked ancestor is the point beyond which map
                // construction must reserve cold prototype copies for the whole leaf.
                if (!checkedTurfDepth) {
                    var depth = 0;
                    for (int? ancestor = id; ancestor is not null; ancestor = Data.Types[ancestor.Value].Parent) {
                        ChargeTurfWork(1);
                        if (++depth > 256) throw new CompatibilityException("Turf initializer inheritance limit exceeded.");
                    }
                    checkedTurfDepth = true;
                }
                ReserveTurfFieldCopy(inherited?.Variables.Keys, inherited?.Variables.Count ?? 0);
                ReserveTurfFieldCopy(type.Variables?.Keys, type.Variables?.Count ?? 0);
            }
            var variables = new Dictionary<string, DmValue>(
                (inherited?.Variables.Count ?? 0) + (type.Variables?.Count ?? 0), StringComparer.Ordinal);
            if (inherited is not null)
                foreach (var pair in inherited.Variables) {
                    if (newTurfPrototype) _startupCancellation.ThrowIfCancellationRequested();
                    variables.Add(pair.Key, pair.Value);
                }
            foreach (var (name, raw) in type.Variables ?? []) {
                if (newTurfPrototype) ChargeTurfWork(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(raw).Length);
                var value = ConvertValue(raw);
                // If conversion later gains mutable values, do not silently share
                // those across instances through this immutable-default cache.
                if (value.Value is not (null or float or string or DmTypeReference or DmResource))
                    throw new CompatibilityException($"Mutable type default '{type.Path}.{name}' cannot be cached.");
                variables[name] = value;
            }
            inherited = new TypeDefaults(
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, DmValue>(variables),
                new DmValue(new DmTypeReference(typeId, type.Path)),
                inherited?.Type ?? DmValue.Null,
                inherited?.ExecutableInitializerType ?? initializer);
            _typeDefaults.Add(typeId, inherited);
        }
        return inherited!;
    }
    private DmValue ConvertValue(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Null => DmValue.Null,
        JsonValueKind.Number => new(value.GetSingle()),
        JsonValueKind.String => DecodeCompilerText(value),
        JsonValueKind.Object => ConvertTaggedValue(value),
        _ => throw new CompatibilityException($"Unsupported compiler value: {value.ValueKind}.")
    };
    private static DmValue DecodeCompilerText(JsonElement value) {
        try { return new(value.GetString()); }
        catch (InvalidOperationException ex) {
            throw new CompatibilityException("Malformed compiler text.", ex);
        }
    }

    private DmValue ConvertTaggedValue(JsonElement value) {
        var kind = value.GetProperty("type").GetInt32();
        if (kind == 0) return new(new DmResource(value.GetProperty("resourcePath").GetString()!));
        if (kind == 1) {
            var id = value.GetProperty("value").GetInt32();
            if (id < 0 || id >= Data.Types.Length) throw new CompatibilityException($"Invalid type value {id}.");
            return new(new DmTypeReference(id, Data.Types[id].Path));
        }
        if (kind == 4) return new(float.PositiveInfinity);
        if (kind == 5) return new(float.NegativeInfinity);
        throw new CompatibilityException($"Compiler value kind {kind} is not implemented.");
    }
    private Procedure? ResolveProc(int typeId, string name) {
        for (int? current = typeId; current is not null; current = Data.Types[current.Value].Parent)
            foreach (var chain in Data.Types[current.Value].Procs ?? [])
                for (var i = chain.Length - 1; i >= 0; i--)
                    if (Data.Procs[chain[i]].Name == name) return Data.Procs[chain[i]];
        return null;
    }
    private Procedure? ResolveInitializer(int typeId) {
        for (int? current = typeId; current is not null; current = Data.Types[current.Value].Parent)
            if (Data.Types[current.Value].InitProc is int init) return Data.Procs[init];
        return null;
    }
    private Procedure? SuperProc(Procedure proc) {
        var owningType = Data.Types[proc.OwningTypeId];
        if (owningType.InitProc is int init && ReferenceEquals(Data.Procs[init], proc))
            return owningType.Parent is int ancestor ? ResolveInitializer(ancestor) : null;
        foreach (var chain in owningType.Procs ?? []) {
            var index = Array.FindIndex(chain, id => ReferenceEquals(Data.Procs[id], proc));
            if (index > 0) return Data.Procs[chain[index - 1]];
        }
        return owningType.Parent is int parent ? ResolveProc(parent, proc.Name) : null;
    }

    private void LoadMaps() {
        var cells = new Dictionary<(int, int, int), WorldTile>();
        var areas = new Dictionary<int, DmObject>();
        var turfInitializers = new Dictionary<int, TurfInitializerPlan>();
        var declaredDirections = new Dictionary<int, int>();
        if ((Data.Maps ?? []).Any(map => map.MaxZ > 0)) {
            var defaultAreaId = World.GetVariableOrDefault("area").Value is DmTypeReference defaultAreaType
                ? defaultAreaType.Id : throw new CompatibilityException("world.area must contain a type reference for default map filling.");
            RequireMapType(defaultAreaId, "/area", "area");
            _defaultArea = CreateMapArea(defaultAreaId);
            areas.Add(defaultAreaId, _defaultArea);
        }
        foreach (var map in Data.Maps ?? []) {
            Width = Math.Max(Width, map.MaxX); Height = Math.Max(Height, map.MaxY); Levels = Math.Max(Levels, map.MaxZ);
            foreach (var block in map.Blocks) {
                if (block.Width <= 0 || block.Height <= 0 || (long)block.Width * block.Height != block.Cells.Length)
                    throw new CompatibilityException("Malformed map block dimensions.");
                var blockMovables = new List<(int X, int Y, DmObject Instance)>();
                for (var i = 0; i < block.Cells.Length; i++) {
                    _startupCancellation.ThrowIfCancellationRequested();
                    var x = block.X + i % block.Width;
                    var y = block.Y + block.Height - 1 - i / block.Width;
                    if (x < 1 || y < 1 || block.Z < 1 || x > map.MaxX || y > map.MaxY || block.Z > map.MaxZ)
                        throw new CompatibilityException("Map block extends outside its declared dimensions.");
                    if (!map.CellDefinitions.TryGetValue(block.Cells[i], out var cell)) throw new CompatibilityException("Missing map cell definition.");
                    var turfId = cell.Turf?.Type ?? (World.GetVariableOrDefault("turf").Value is DmTypeReference defaultTurf
                        ? defaultTurf.Id : throw new CompatibilityException("world.turf must contain a type reference for default map filling."));
                    RequireMapType(turfId, "/turf", "turf");
                    var turf = CreateMapTurf(turfId, cell.Turf?.VarOverrides, turfInitializers);
                    if (cell.Area?.VarOverrides is not null)
                        throw new CompatibilityException("Map area variable overrides are not implemented.");
                    var areaId = cell.Area?.Type ?? (World.GetVariableOrDefault("area").Value is DmTypeReference defaultArea
                        ? defaultArea.Id : throw new CompatibilityException("world.area must contain a type reference for default map filling."));
                    RequireMapType(areaId, "/area", "area");
                    if (!areas.TryGetValue(areaId, out var area)) {
                        area = CreateMapArea(areaId);
                        areas.Add(areaId, area);
                    }
                    if (cells.ContainsKey((x, y, block.Z))) throw new CompatibilityException("Overlapping map blocks are not implemented.");
                    cells[(x, y, block.Z)] = new(x, y, block.Z, turf.GetVariableOrDefault("density").IsTruthy, turf.TypePath, turf.GetVariableOrDefault("color").Value as string);
                    _mapLocations.Add(turf, new(x, y, block.Z, area));
                    _mapTurfs.Add((x, y, block.Z), turf);
                    var entities = new List<DmObject>();
                    foreach (var entity in cell.Objects) {
                        _startupCancellation.ThrowIfCancellationRequested();
                        RequireMapType(entity.Type, "/atom/movable", "entity");
                        var instance = CreateInstance(entity.Type, initializeLater: true);
                        ApplyMapEntityOverrides(instance, entity.VarOverrides);
                        if (ResolveInitializer(entity.Type) is { } initializer)
                            ValidateMapInitializer(instance, initializer, new HashSet<Procedure>(), "Movable");
                        // Validate every mapped initializer before Boot can produce effects.
                        // Execution is deferred to this instance's position in New order.
                        var snapshot = new MapEntity(x, y, block.Z, instance.TypePath) {
                            Direction = PlacedDirection(instance, entity.VarOverrides?.ContainsKey("dir") == true, declaredDirections),
                            PixelX = ReadMapPixelOffset(instance, "pixel_x"),
                            PixelY = ReadMapPixelOffset(instance, "pixel_y")
                        };
                        _mapLocations.Add(instance, new(x, y, block.Z, turf,
                            NameOverridden: entity.VarOverrides?.ContainsKey("name") == true,
                            InvisibilityOverridden: entity.VarOverrides?.ContainsKey("invisibility") == true,
                            MovableSnapshot: snapshot));
                        entities.Add(instance);
                        blockMovables.Add((x, y, instance));
                        _entities.Add(snapshot);
                    }
                    _mapCells.Add(new(x, y, block.Z, turf, area, entities.AsReadOnly()));
                }
                _mapMovableNewOrder.AddRange(blockMovables.OrderBy(m => m.Y).ThenBy(m => m.X).Select(m => m.Instance));
            }
        }
        if ((long)Width * Height * Levels != cells.Count)
            throw new CompatibilityException("Sparse map default filling is not implemented.");
        _tiles.AddRange(cells.Values.OrderBy(t => t.Z).ThenBy(t => t.Y).ThenBy(t => t.X));
        _mapCells.Sort((a, b) => (a.Z, a.Y, a.X).CompareTo((b.Z, b.Y, b.X)));
        // Single-level area coordinates use lowest y, then x. Multi-level upstream
        // coordinate caching is not a stable contract here, so reject those reads.
        foreach (var cell in _mapCells) {
            _startupCancellation.ThrowIfCancellationRequested();
            if (_mapLocations.TryGetValue(cell.Area, out var first) && first.Z != cell.Z)
                _multiLevelAreas.Add(cell.Area);
            _mapLocations.TryAdd(cell.Area, new(cell.X, cell.Y, cell.Z, null));
        }
        if (_defaultArea is not null) _mapLocations.TryAdd(_defaultArea, new(0, 0, 0, null));
    }

    private sealed record TurfInitializerPlan(Procedure Entry, HashSet<string> Writes,
        HashSet<string> ConstFields, bool WritesVariables, long InstanceWork, long MaterializationWork);

    private void ReserveTurfFieldCopy(IEnumerable<string>? names, int count) {
        ChargeTurfWork(1L + count);
        if (names is null) return;
        foreach (var name in names) ChargeTurfWork(name.Length);
    }

    private void ChargeTurfWork(long count) {
        _startupCancellation.ThrowIfCancellationRequested();
        if (count < 0 || count > _budget) throw new CompatibilityException("Mapped turf initializer construction work budget exhausted.");
        _budget -= (int)count;
    }

    private DmObject CreateMapTurf(int typeId, Dictionary<string, JsonElement>? overrides,
        Dictionary<int, TurfInitializerPlan> plans) {
        var defaults = GetTypeDefaults(typeId, initializeLater: true, mappedTurf: true);
        var turf = new DmObject(typeId, Data.Types[typeId].Path, defaults.Variables, defaults.Type, defaults.ParentType);
        TurfInitializerPlan? plan = null;
        if (defaults.ExecutableInitializerType is not null) {
            if (!plans.TryGetValue(typeId, out plan)) {
                plan = ValidateTurfInitializer(turf, defaults.Variables);
                plans.Add(typeId, plan);
            }
            // Ordinary writes copy inherited fields. Transform uses its own per-atom storage.
            var materialization = plan.WritesVariables || overrides is { Count: > 0 } ? plan.MaterializationWork : 0;
            ChargeTurfWork(plan.InstanceWork + materialization);
        }
        foreach (var (name, value) in overrides ?? []) {
            if (plan is not null) {
                ChargeTurfWork(1L + name.Length);
                if (plan.Writes.Contains(name))
                    throw new CompatibilityException($"Map turf override '{name}' overlaps an executable initializer assignment.");
                if (plan.ConstFields.Contains(name))
                    throw new CompatibilityException($"Map turf override '{name}' targets a const field.");
            }
            if (!turf.HasVariable(name))
                throw new CompatibilityException($"Map turf override '{name}' names an unknown variable.");
            var protectedField = plan is null ? ProtectedMapField(turf, name) : ProtectedAtomField(name);
            if ((protectedField && name is not ("density" or "color" or "name" or "layer")) ||
                name is "type" or "parent_type" or "vars" or "tag")
                throw new CompatibilityException($"Map turf override '{name}' is not implemented.");
            if (plan is not null) ChargeTurfWork(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value).Length);
            turf.SetVariable(name, ConvertValue(value));
        }
        if (plan is not null) Execute(new Fiber(new Frame(plan.Entry, turf, [])));
        return turf;
    }

    // Each body calls its compiler super first, then assigns complete supported expressions.
    // Validate all bodies against the concrete leaf before executing any part of the chain.
    private TurfInitializerPlan ValidateTurfInitializer(DmObject turf, IReadOnlyDictionary<string, DmValue> defaults) {
        var bodies = new Stack<Procedure>();
        var constFields = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);
        var writesVariables = false;
        var isAtom = false;
        var depth = 0;
        for (int? id = turf.TypeId; id is int typeId; id = Data.Types[typeId].Parent) {
            ChargeTurfWork(1);
            if (++depth > 256) throw new CompatibilityException("Turf initializer inheritance limit exceeded.");
            var type = Data.Types[typeId];
            isAtom |= type.Path == "/atom";
            if (type.Path is "/atom/movable" or "/mob")
                throw new CompatibilityException($"Turf initializer has incompatible builtin ancestor {type.Path}.");
            if (type.ConstVariables is not null)
                foreach (var name in type.ConstVariables) {
                    ChargeTurfWork(1L + name.Length);
                    constFields.Add(name);
                }
            if (type.InitProc is not int procId) continue;
            var proc = Data.Procs[procId];
            if (proc.OwningTypeId != typeId || proc.Name != "<init>" || proc.Attributes != 0 ||
                proc.MaxVariableId != 0 || proc.Arguments is { Length: > 0 } || proc.Bytecode is not { Length: > 0 })
                throw new CompatibilityException($"Turf initializer on {type.Path} has unsupported procedure metadata.");
            bodies.Push(proc);
        }
        long instanceWork = 0;
        Procedure? entry = null;
        foreach (var proc in bodies) {
            entry = proc;
            var bytes = proc.Bytecode!;
            ChargeTurfWork(1L + bytes.Length + (proc.SourceInfo?.Length ?? 0));
            // The VM allocates at least 256 locals per frame. Charging the byte stream
            // also bounds literal reads and packed pushes that share one VM dispatch.
            instanceWork += 256L + bytes.Length + depth;
            var reader = new ByteReader(bytes);
            CompatibilityException FaultAt(int offset, string detail) =>
                new($"Turf initializer {detail} at {Project.Location(proc, offset)}");
            if ((DreamProcOpcode)reader.Byte() != DreamProcOpcode.Call || reader.Reference().Kind != 6 ||
                reader.Byte() != 0 || reader.Int() != 0)
                throw FaultAt(0, "must begin with an argument-free super call");
            // The header's implicit result cannot be consumed as a literal or used for a write.
            // Nonnegative operands are literal type IDs. Only literal finite numbers may
            // feed a matrix call, and its result must go directly to src.transform.
            const int literal = -1, finiteNumber = -2, matrixResult = -3;
            var operands = new List<int>();
            var needsPop = false;
            while (reader.Position < reader.Length) {
                _startupCancellation.ThrowIfCancellationRequested();
                var offset = reader.Position;
                var op = (DreamProcOpcode)reader.Byte();
                if (needsPop) {
                    if (op != DreamProcOpcode.Pop) throw FaultAt(offset, "must discard the previous assignment");
                    needsPop = false;
                    continue;
                }
                if (operands.Count == 1 && operands[0] == matrixResult &&
                    op is not (DreamProcOpcode.Assign or DreamProcOpcode.AssignNoPush))
                    throw FaultAt(offset, "must assign the matrix result directly to transform");
                switch (op) {
                    case DreamProcOpcode.PushNull: operands.Add(literal); break;
                    case DreamProcOpcode.PushFloat:
                        operands.Add(float.IsFinite(reader.Float()) ? finiteNumber : literal); break;
                    case DreamProcOpcode.PushString: case DreamProcOpcode.PushResource:
                        Project.String(reader.Int()); operands.Add(literal); break;
                    case DreamProcOpcode.PushType:
                        var typeId = reader.Int();
                        if ((uint)typeId >= (uint)Data.Types.Length) throw FaultAt(offset, "uses an invalid type literal");
                        operands.Add(typeId); break;
                    case DreamProcOpcode.PushNFloats: case DreamProcOpcode.PushNStrings:
                    case DreamProcOpcode.CreateListNFloats: case DreamProcOpcode.CreateListNStrings: {
                        var count = reader.Int();
                        if (count < 0 || count > 256 || count > (reader.Length - reader.Position) / 4)
                            throw FaultAt(offset, "packed literal count is invalid or exceeds 256");
                        var push = op is DreamProcOpcode.PushNFloats or DreamProcOpcode.PushNStrings;
                        for (var i = 0; i < count; i++) {
                            _startupCancellation.ThrowIfCancellationRequested();
                            var operand = literal;
                            if (op is DreamProcOpcode.PushNStrings or DreamProcOpcode.CreateListNStrings) Project.String(reader.Int());
                            else if (float.IsFinite(reader.Float())) operand = finiteNumber;
                            if (push) operands.Add(operand);
                        }
                        if (!push) operands.Add(literal);
                        break;
                    }
                    case DreamProcOpcode.CreateList: {
                        var count = reader.Int();
                        if (count < 0 || count > 256 || count > operands.Count)
                            throw FaultAt(offset, "list literal count is invalid or exceeds 256");
                        operands.RemoveRange(operands.Count - count, count);
                        operands.Add(literal);
                        break;
                    }
                    case DreamProcOpcode.CreateAssociativeList:
                        if (reader.Int() != 1 || operands.Count < 2 || operands[^2] < 0)
                            throw FaultAt(offset, "supports only one type-literal associative key");
                        instanceWork += Data.Types[operands[^2]].Path.Length;
                        operands.RemoveRange(operands.Count - 2, 2);
                        operands.Add(literal); break;
                    case DreamProcOpcode.Assign: case DreamProcOpcode.AssignNoPush: {
                        var target = reader.Reference();
                        if (operands.Count != 1 || target.Kind != 13)
                            throw FaultAt(offset, "must assign exactly one literal to an own user field");
                        var name = Project.String(target.Value);
                        ChargeTurfWork(1L + name.Length + depth);
                        if (operands[0] == matrixResult && name != "transform")
                            throw FaultAt(offset, "must assign the matrix result directly to transform");
                        var matrixTransform = operands[0] == matrixResult;
                        if (matrixTransform && (!isAtom || !defaults.TryGetValue(name, out var raw) || !raw.IsNull))
                            throw FaultAt(offset, "requires a declared null transform on an atom");
                        if (!turf.HasVariable(name) || constFields.Contains(name) ||
                            (ProtectedAtomField(name) && !matrixTransform) || name is "type" or "parent_type" or "vars" or "tag")
                            throw FaultAt(offset, $"cannot assign native, const, metadata or unknown field '{name}'");
                        instanceWork += 1L + name.Length;
                        writes.Add(name);
                        writesVariables |= !matrixTransform;
                        operands.Clear();
                        needsPop = op == DreamProcOpcode.Assign;
                        break;
                    }
                    case DreamProcOpcode.Call:
                        var reference = reader.Reference();
                        if (reference.Kind == 11 && reference.Value >= 0 && reference.Value == Project.NativeMatrixProcId) {
                            var mode = reader.Byte();
                            var count = reader.Int();
                            if (!IsMatrixCallShape(mode, count) || operands.Count != count ||
                                !operands.TrueForAll(value => value == finiteNumber))
                                throw FaultAt(offset, "matrix requires zero arguments or six finite numeric literals");
                            operands.Clear();
                            operands.Add(matrixResult);
                            break;
                        }
                        var callee = reference.Kind == 11 && (uint)reference.Value < (uint)Data.Procs.Length
                            ? Data.Procs[reference.Value].Name : "indirect or repeated super";
                        throw FaultAt(offset, $"call '{callee}' is not implemented");
                    default:
                        throw FaultAt(offset, $"opcode {op} is not implemented");
                }
            }
            if (needsPop || operands.Count != 0) throw FaultAt(reader.Length, "ends with an incomplete assignment");
        }
        // MaterializeVariables copies each key, checks the two metadata keys, then sets both.
        ChargeTurfWork(1L + defaults.Count);
        long materializationWork = defaults.Count + 2L + 2 * ("type".Length + "parent_type".Length);
        foreach (var name in defaults.Keys) {
            _startupCancellation.ThrowIfCancellationRequested();
            materializationWork += name.Length;
        }
        return new TurfInitializerPlan(entry!, writes, constFields, writesVariables, instanceWork, materializationWork);
    }

    private static DmValue TurfLayer(DmObject turf) {
        // Pinned native storage preserves every float (including infinities), and
        // nonnumbers coerce to zero. No rendering operation or integer cast occurs.
        return turf.GetVariableOrDefault("layer").Value is float number ? number : 0f;
    }

    private bool IsSubtypeOf(int typeId, string ancestorPath) {
        if (typeId < 0 || typeId >= Data.Types.Length) return false;
        for (int? current = typeId; current is not null; current = Data.Types[current.Value].Parent)
            if (Data.Types[current.Value].Path == ancestorPath) return true;
        return false;
    }
    private void ApplyMapEntityOverrides(DmObject instance, Dictionary<string, JsonElement>? overrides) {
        foreach (var (name, raw) in (overrides ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            var appearanceOverride = name is "desc" or "dir" or "icon" or "invisibility" or "pixel_x" or "pixel_y";
            if (appearanceOverride)
                ChargeMapAppearanceWork(1 + System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(raw).Length, name);
            var category = raw.ValueKind.ToString();
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("type", out var tag) && tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out var kind))
                category = $"compiler tag {kind}";
            CompatibilityException Failure(string reason, Exception? inner = null) =>
                new($"Map entity override '{name}' ({category}) on {instance.TypePath}: {reason.TrimEnd('.')}.", inner);
            if (name is "type" or "parent_type" or "vars" or "tag")
                throw Failure("metadata/tag override is not implemented");
            if (!instance.HasVariable(name))
                throw Failure("unknown or non-instance field is not implemented");
            if (appearanceOverride) {
                try { ApplyMapAppearanceOverride(instance, name, raw); }
                catch (CompatibilityException ex) { throw Failure(ex.Message, ex); }
                continue;
            }
            for (int? typeId = instance.TypeId; typeId is not null; typeId = Data.Types[typeId.Value].Parent)
                if (Data.Types[typeId.Value].ConstVariables?.Contains(name, StringComparer.Ordinal) == true)
                    throw Failure("const field override is not implemented");
            if (ProtectedMapField(instance, name) && name is not ("name" or "icon_state"))
                throw Failure("native field override is not implemented");
            try { instance.SetVariable(name, ConvertValue(raw)); }
            catch (CompatibilityException ex) { throw Failure(ex.Message, ex); }
        }
    }

    private void ChargeMapIconWork(int count) => ChargeMapAppearanceWork(count, "icon");

    private void ChargeMapAppearanceWork(int count, string field) {
        _startupCancellation.ThrowIfCancellationRequested();
        if (count < 0 || count > _budget) throw new CompatibilityException($"Map {field} work budget exhausted.");
        _budget -= count;
    }

    private void ValidateMapAppearanceOwner(DmObject obj, string name, bool writing) {
        var atom = false;
        var movable = false;
        for (int? id = obj.TypeId; id is int current; id = Data.Types[current].Parent) {
            ChargeMapAppearanceWork(1, name);
            var type = Data.Types[current];
            if (type.Path is "/area" or "/turf" || (type.Path == "/atom" && !movable))
                throw new CompatibilityException($"Map {name} requires a declared {name} on a mapped movable atom.");
            movable |= type.Path == "/atom/movable";
            atom |= type.Path == "/atom";
            if (writing && type.ConstVariables is not null)
                foreach (var field in type.ConstVariables) {
                    ChargeMapAppearanceWork(1, name);
                    if (field == name) throw new CompatibilityException("const field override is not implemented");
                }
        }
        if (!atom || !movable || !obj.HasVariable(name))
            throw new CompatibilityException($"Map {name} requires a declared {name} on a mapped movable atom.");
    }

    private DmResource ResolveMapIcon(string token) =>
        _mapIcons?.Resolve(token, ChargeMapIconWork) ??
        throw new CompatibilityException("Map icon requires an approved DMI resource inventory.");

    private DmValue DecodeMapIcon(JsonElement raw) {
        // The compiler's tagged resource is a closed pair. Its token is opaque;
        // only an exact, separately approved entry names an asset.
        if (raw.ValueKind != JsonValueKind.Object || raw.GetPropertyCount() != 2 ||
            !raw.TryGetProperty("type", out var tag) || tag.ValueKind != JsonValueKind.Number ||
            !tag.TryGetInt32(out var kind) || kind != 0 || !raw.TryGetProperty("resourcePath", out var path) ||
            path.ValueKind != JsonValueKind.String)
            throw new CompatibilityException("Map icon override requires a tagged DMI resource.");
        // An escaped JSON code unit uses up to six bytes. Bound decoding before GetString().
        if (System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(path).Length > 6 * DmFileInventory.MaximumPathLength + 2)
            throw new CompatibilityException("Map icon compiler token length exceeds the supported limit.");
        return new(ResolveMapIcon(path.GetString()!));
    }

    private static DmValue NormalizeMapInvisibility(DmValue raw, bool declared) {
        if (raw.Value is not float n || !float.IsFinite(n))
            throw new CompatibilityException("Map invisibility requires a finite number.");
        // DreamMaker checks type declarations before truncation. Placed DMM
        // values instead use the native signed appearance range.
        if (declared && (n < 0 || n > 101))
            throw new CompatibilityException("Mapped movable declared invisibility must be between 0 and 101.");
        return (float)(int)Math.Clamp(n, -127f, 127f);
    }

    private static int DeclaredMapDirection(DmValue raw) {
        if (raw.Value is not float n || !float.IsFinite(n) || n < 0)
            throw new CompatibilityException("Map dir declaration requires a finite nonnegative number.");
        // The compiler keeps only the eight compass facings on a declaration.
        var direction = (int)Math.Min(n, 255f);
        return direction is 1 or 2 or 4 or 5 or 6 or 8 or 9 or 10 ? direction : MapEntity.South;
    }

    private static DmValue DecodeMapDirection(DmObject obj, JsonElement raw) {
        var inherited = DeclaredMapDirection(obj.GetVariableOrDefault("dir"));
        if (raw.ValueKind == JsonValueKind.Null) return inherited;
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetSingle(out var n) || !float.IsFinite(n))
            throw new CompatibilityException("Map dir requires a finite number or null.");
        // A placement can retain any positive byte, even opposing direction bits.
        return n < 1 ? inherited : (int)Math.Min(n, 255f);
    }

    private int PlacedDirection(DmObject obj, bool overridden, Dictionary<int, int> declaredDirections) {
        ChargeMapAppearanceWork(1, "dir");
        if (overridden) return (int)obj.GetVariableOrDefault("dir").Number;
        if (!declaredDirections.TryGetValue(obj.TypeId, out var direction)) {
            ValidateMapAppearanceOwner(obj, "dir", writing: false);
            direction = DeclaredMapDirection(obj.GetVariableOrDefault("dir"));
            declaredDirections.Add(obj.TypeId, direction);
        }
        return direction;
    }

    // BYOND uses symmetric signed limits for both axes, including at -32768.
    private static int NormalizePixelOffset(float number) =>
        (int)Math.Clamp(number, -32767f, 32767f);

    private int ReadMapPixelOffset(DmObject obj, string name) {
        ChargeMapAppearanceWork(1, name);
        if (!obj.TryGetVariable(name, out var value))
            throw new CompatibilityException($"Map {name} requires a declared {name} on a mapped movable atom.");
        if (value.Value is not float number || !float.IsFinite(number))
            throw new CompatibilityException($"Map {name} declaration requires a finite number.");
        return NormalizePixelOffset(number);
    }

    private DmValue DecodeMapPixelOffset(DmObject obj, string name, JsonElement raw) {
        // A map placement may clear either axis. A null type declaration is invalid.
        _ = ReadMapPixelOffset(obj, name);
        if (raw.ValueKind == JsonValueKind.Null) return 0;
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetSingle(out var number) || !float.IsFinite(number))
            throw new CompatibilityException($"Map {name} requires a finite number or null.");
        return NormalizePixelOffset(number);
    }

    private void ApplyMapAppearanceOverride(DmObject obj, string name, JsonElement raw) {
        ValidateMapAppearanceOwner(obj, name, writing: true);
        var value = name switch {
            "desc" when raw.ValueKind is JsonValueKind.String or JsonValueKind.Null => ConvertValue(raw),
            "desc" => throw new CompatibilityException("Map desc requires text or null."),
            "dir" => DecodeMapDirection(obj, raw),
            "pixel_x" or "pixel_y" => DecodeMapPixelOffset(obj, name, raw),
            "icon" => DecodeMapIcon(raw),
            "invisibility" when raw.ValueKind == JsonValueKind.Number && raw.TryGetSingle(out var number) =>
                NormalizeMapInvisibility(new DmValue(number), declared: false),
            "invisibility" => throw new CompatibilityException("Map invisibility requires a numeric literal."),
            _ => throw new CompatibilityException($"Unsupported map appearance field '{name}'.")
        };
        if (obj.DeferredVariables is { } defaults) {
            // The first write materializes every inherited key and the two metadata keys.
            ChargeMapAppearanceWork(1 + defaults.Count + 2 * ("type".Length + "parent_type".Length), name);
            foreach (var key in defaults.Keys) ChargeMapAppearanceWork(key.Length, name);
        }
        obj.SetVariable(name, value);
    }

    private DmValue ReadMapIcon(DmObject obj) {
        ValidateMapAppearanceOwner(obj, "icon", writing: false);
        var raw = obj.GetVariableOrDefault("icon");
        if (raw.IsNull) return raw;
        if (raw.Value is DmResource resource) return new(ResolveMapIcon(resource.Path));
        throw new CompatibilityException("Reading a non-resource mapped movable icon is not implemented.");
    }

    private string FormatValue(DmValue value) {
        if (_mapIcons is null) return value.ToString();
        // Datum output follows its name. Apply the same resource rule at the end
        // of the chain and bound cycles without recursively calling ToString().
        while (value.Value is DmObject { IsNativeMatrix: false } obj &&
            obj.TryGetVariable("name", out var name) && !name.IsNull) {
            ChargeMapIconWork(1);
            value = name;
        }
        if (value.Value is DmResource resource && _mapIcons.ContainsToken(resource.Path, ChargeMapIconWork))
            throw new CompatibilityException("Stringification of a mapped DMI resource is not implemented.");
        return value.ToString();
    }

    private void RequireMapType(int typeId, string ancestorPath, string role) {
        if (!IsSubtypeOf(typeId, ancestorPath))
            throw new CompatibilityException($"Map {role} must be a subtype of {ancestorPath}.");
    }

    // Area initialization is currently limited to local literal/list defaults. Validate
    // the complete super chain before executing anything: no observable preboot effects.
    private DmObject CreateMapArea(int typeId) {
        var area = CreateInstance(typeId, initializeLater: true);
        var initializer = ResolveInitializer(typeId);
        if (initializer is null) return area;
        ValidateMapInitializer(area, initializer, new HashSet<Procedure>(), "Area");
        Execute(new Fiber(new Frame(initializer, area, [])));
        return area;
    }

    private void ValidateMapInitializer(DmObject area, Procedure proc, HashSet<Procedure> visited, string role) {
        if (!visited.Add(proc)) throw new CompatibilityException($"{role} initializer repeats a super initializer at {Project.Location(proc, 0)}.");
        var frame = new Frame(proc, area, []);
        var reader = frame.Reader;
        if (visited.Count > 256) throw Fault(frame, 0, $"{role} initializer inheritance limit exceeded.");
        if (Data.Types[proc.OwningTypeId].InitProc is not int ownInit || !ReferenceEquals(Data.Procs[ownInit], proc))
            throw Fault(frame, 0, $"{role} initializer must be a compiler type initializer.");
        void Assignment(Reference reference, int offset) {
            if (reference.Kind != 13) throw Fault(frame, offset, $"{role} initializer may assign only its own user variables.");
            var name = Project.String(reference.Value);
            if (!area.HasVariable(name) || ProtectedMapField(area, name) || name is "type" or "parent_type" or "vars" or "tag")
                throw Fault(frame, offset, $"{role} initializer native/metadata assignment '{name}' is not implemented.");
        }
        while (reader.Position < reader.Length) {
            var offset = reader.Position;
            var opcode = (DreamProcOpcode)reader.Byte();
            switch (opcode) {
                case DreamProcOpcode.PushNull: case DreamProcOpcode.Pop: case DreamProcOpcode.Return: break;
                case DreamProcOpcode.PushFloat: case DreamProcOpcode.ReturnFloat: reader.Float(); break;
                case DreamProcOpcode.PushString: case DreamProcOpcode.PushResource: Project.String(reader.Int()); break;
                case DreamProcOpcode.PushType:
                    var id = reader.Int();
                    if (id < 0 || id >= Data.Types.Length) throw Fault(frame, offset, $"{role} initializer has an invalid type literal.");
                    break;
                case DreamProcOpcode.Assign: case DreamProcOpcode.AssignNoPush: case DreamProcOpcode.NullRef: Assignment(reader.Reference(), offset); break;
                case DreamProcOpcode.ReturnReferenceValue:
                    if (reader.Reference().Kind != 2) throw Fault(frame, offset, $"{role} initializer reference reads are not implemented.");
                    break;
                case DreamProcOpcode.CreateList: case DreamProcOpcode.CreateAssociativeList: case DreamProcOpcode.CreateStrictAssociativeList:
                    if (reader.Int() < 0) throw Fault(frame, offset, $"{role} initializer has an invalid list length.");
                    break;
                case DreamProcOpcode.CreateMultidimensionalList:
                    var dimensions = reader.Int();
                    if (dimensions < 0 || dimensions > 32)
                        throw Fault(frame, offset, $"{role} initializer dimension count exceeds the supported limit.");
                    break;
                case DreamProcOpcode.PushNFloats: case DreamProcOpcode.PushNStrings:
                case DreamProcOpcode.CreateListNFloats: case DreamProcOpcode.CreateListNStrings: case DreamProcOpcode.CreateListNResources:
                    var count = reader.Int();
                    if (count < 0 || count > (reader.Length - reader.Position) / 4)
                        throw Fault(frame, offset, $"{role} initializer has an invalid literal list length.");
                    for (var i = 0; i < count; i++) {
                        if (opcode is DreamProcOpcode.CreateListNFloats or DreamProcOpcode.PushNFloats) reader.Float();
                        else Project.String(reader.Int());
                    }
                    break;
                case DreamProcOpcode.PushNOfStringFloats:
                    var pairCount = reader.Int();
                    if (pairCount < 0 || pairCount > (reader.Length - reader.Position) / 8)
                        throw Fault(frame, offset, $"{role} initializer has an invalid string/float literal count.");
                    for (var i = 0; i < pairCount; i++) { Project.String(reader.Int()); reader.Float(); }
                    break;
                case DreamProcOpcode.PushStringFloat:
                    Project.String(reader.Int()); reader.Float(); break;
                case DreamProcOpcode.Call:
                    var reference = reader.Reference();
                    var kind = reader.Byte(); var argc = reader.Int();
                    if (reference.Kind != 6 || kind is not (0 or 4) || argc != 0)
                        throw Fault(frame, offset, $"{role} initializer calls other than argument-free super initialization are not implemented.");
                    if (SuperProc(proc) is { } parent) ValidateMapInitializer(area, parent, visited, role);
                    break;
                default:
                    throw Fault(frame, offset, $"{role} initializer opcode {opcode} is not implemented; only local literal/list defaults are supported.");
            }
        }
    }

    private DmList SplitText(DmValue[] args) {
        if (args.Length is < 2 or > 5)
            throw new CompatibilityException("splittext requires two to five arguments.");
        DmList Empty() { ChargeListAllocation(1); return new DmList(); }
        if (args[0].Value is not string text || text.Length == 0) return Empty();
        var fullStart = args.Length < 3 || args[2].IsNull || args[2].Value is float startValue && startValue == 1f;
        var fullEnd = args.Length < 4 || args[3].IsNull || args[3].Value is float endValue && endValue == 0f;
        if (!fullStart || !fullEnd)
            throw new CompatibilityException("splittext indexed search bounds are not implemented.");
        var include = false;
        if (args.Length > 4 && !args[4].IsNull) {
            if (args[4].Value is not float flag || (flag != 0f && flag != 1f))
                throw new CompatibilityException("splittext include_delimiters supports only zero or one.");
            include = flag == 1f;
        }
        if (args.Length < 2 || args[1].Value is not string delimiter)
            throw new CompatibilityException("splittext non-string delimiter forms are not implemented.");
        void Work(int amount) {
            if (amount < 0 || amount > _budget)
                throw new CompatibilityException("splittext work budget exhausted.");
            _budget -= amount;
        }
        void ValidateText(string value) {
            Work(value.Length);
            for (var i = 0; i < value.Length; i++) {
                if (!char.IsSurrogate(value[i])) continue;
                if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i]))
                    throw new CompatibilityException("splittext malformed UTF-16 input is not implemented.");
            }
        }
        if (_textFiles is null || !_textFiles.TryConfirmDecodedText(text, Work)) ValidateText(text);
        ValidateText(delimiter);
        if (delimiter.Length == 0) {
            Work(text.Length);
            foreach (var character in text)
                if (character > 127) throw new CompatibilityException("splittext empty delimiter supports ASCII input only.");
        }
        ChargeListAllocation(1);
        var result = new DmList();
        void Emit(string source, int offset, int length) {
            Work(length);
            var bytes = 2L * length;
            if (bytes > _splitTextOutputBytesRemaining)
                throw new CompatibilityException("splittext output allocation budget exhausted.");
            ChargeListAllocation(1);
            _splitTextOutputBytesRemaining -= (int)bytes;
            result.Values.Add(new DmValue(source.Substring(offset, length)));
        }
        if (delimiter.Length == 0) {
            for (var i = 0; i < text.Length; i++) {
                if (include && i != 0) Emit(delimiter, 0, 0);
                Emit(text, i, 1);
            }
            return result;
        }
        var start = 0; var position = 0;
        while (position <= text.Length - delimiter.Length) {
            var matched = 0;
            while (matched < delimiter.Length) {
                Work(1);
                if (text[position + matched] != delimiter[matched]) break;
                matched++;
            }
            if (matched == delimiter.Length) {
                Emit(text, start, position - start);
                if (include) Emit(delimiter, 0, delimiter.Length);
                position += delimiter.Length;
                start = position;
            } else position++;
        }
        Emit(text, start, text.Length - start);
        return result;
    }

    private DmValue FileText(DmValue value) {
        if (value.Value is DmResource)
            throw new CompatibilityException("file2text embedded resource bytes are not represented by the text capability.");
        if (value.Value is not string path) return DmValue.Null;
        if (_textFiles is null)
            throw new CompatibilityException("file2text requires an explicitly configured allowlisted text snapshot.");
        void Work(int amount) {
            if (amount < 0 || amount > _budget)
                throw new CompatibilityException("file2text work budget exhausted.");
            _budget -= amount;
        }
        var result = _textFiles.ReadText(path, Work);
        if (result is null) return DmValue.Null;
        Work(result.Length); // Charge every immutable returned UTF-16 code unit before publication.
        return new(result);
    }

    private DmValue FileExists(DmValue value) {
        // Compiler embedded resources are distinct from native file() references;
        // the latter API is unsupported. Measured embedded text resources return null.
        if (value.Value is not string path) return DmValue.Null;
        if (_fileInventory is null)
            throw new CompatibilityException("fexists requires an explicitly configured tracked-source filesystem snapshot.");
        void Work(int amount) {
            if (amount < 0 || amount > _budget)
                throw new CompatibilityException("fexists path work budget exhausted.");
            _budget -= amount;
        }
        return new(_fileInventory.ContainsPath(path, Work) ? 1f : 0f);
    }

    private bool IsOrdinaryDatumType(int typeId) {
        var datum = false;
        for (int? current = typeId; current is not null; current = Data.Types[current.Value].Parent) {
            var path = Data.Types[current.Value].Path;
            if (path == "/datum") datum = true;
            if (path is "/atom" or "/world" or "/client" or "/list" or "/savefile" or "/icon" or
                "/matrix" or "/regex" or "/filter" or "/sound" or "/image" or "/mutable_appearance" or
                "/generator" or "/particles" or "/database" or "/database/query" or
                "/vector" or "/callee" or "/exception" or "/alist")
                return false;
        }
        return datum;
    }
    private bool CanExecuteEmpty(Procedure proc) {
        // The pinned JSON has no native flag. Its native registry has no custom
        // ordinary-datum owners; inherited stubs retain their built-in owner.
        return proc.Name is "New" or "Del" ||
            (Data.Types[proc.OwningTypeId].Path != "/datum" && IsOrdinaryDatumType(proc.OwningTypeId));
    }
    private void BeginConstruction(Fiber fiber, Frame caller, int typeId, DmValue[] args) {
        if (typeId < 0 || typeId >= Data.Types.Length) throw new CompatibilityException("Invalid construction type ID.");
        if (!IsOrdinaryDatumType(typeId))
            throw new CompatibilityException($"Construction of native/atom type {Data.Types[typeId].Path} is not implemented.");
        var instance = CreateInstance(typeId, initializeLater: true);
        PushFrame(fiber, new Frame(caller.Proc, instance, args) {
            Usr = caller.Usr, Construction = new(instance, ResolveInitializer(typeId), ResolveProc(typeId, "New"))
        });
    }
    private static void PushFrame(Fiber fiber, Frame frame) {
        if (fiber.Frames.Count >= 512) throw new CompatibilityException("Procedure call depth limit exceeded.");
        fiber.Frames.Add(frame);
    }
    private void Execute(Fiber fiber) {
        long unlimitedDispatches = long.MaxValue;
        ExecuteDispatches(fiber, ref unlimitedDispatches);
    }

    // False means the host paused before a dispatch. A DM sleep follows the
    // ordinary scheduler path and returns true, with its continuation enqueued.
    private bool ExecuteDispatches(Fiber fiber, ref long remainingDispatches) {
        while (fiber.Frames.Count > 0) {
            _startupCancellation.ThrowIfCancellationRequested();
            if (remainingDispatches == 0) return false;
            var frame = fiber.Frames[^1];
            var child = fiber.ImmediateChild;
            var offset = child is null ? frame.Reader.Position : fiber.ImmediateSpawnOffset;
            if (child is null) {
                remainingDispatches--;
                if (--_budget < 0) throw Fault(frame, offset, "Instruction budget exhausted; possible non-yielding loop.");
            }
            try {
                if (child is not null) {
                    // The spawn opcode was already charged. Finish its child before
                    // the parent dispatches again, retaining the spawn site for faults.
                    _immediateSpawnDepth++;
                    try {
                        if (!ExecuteDispatches(child, ref remainingDispatches)) return false;
                    } finally { _immediateSpawnDepth--; }
                    fiber.ImmediateChild = null;
                    continue;
                }
                if (frame.Construction is { } construction) {
                    Procedure? next = construction.Stage++ switch {
                        0 => construction.Initializer,
                        1 => construction.Constructor,
                        _ => null
                    };
                    if (construction.Stage > 2) { Return(fiber, new(construction.Instance)); continue; }
                    if (next is not null)
                        PushFrame(fiber, new Frame(next, construction.Instance,
                            construction.Stage == 1 ? [] : frame.Arguments) { Usr = frame.Usr });
                    continue;
                }
                if (offset == frame.Reader.Length) { Return(fiber, frame.Result); continue; }
                var opcode = (DreamProcOpcode)frame.Reader.Byte();
                switch (opcode) {
                    case DreamProcOpcode.PushNull: frame.Push(DmValue.Null); break;
                    case DreamProcOpcode.PushFloat: frame.Push(frame.Reader.Float()); break;
                    case DreamProcOpcode.PushString: frame.Push(Project.String(frame.Reader.Int())); break;
                    case DreamProcOpcode.PushType: { var id = frame.Reader.Int(); frame.Push(new(new DmTypeReference(id, Data.Types[id].Path))); break; }
                    case DreamProcOpcode.PushResource: frame.Push(new(new DmResource(Project.String(frame.Reader.Int())))); break;
                    case DreamProcOpcode.PushReferenceValue: frame.Push(Get(frame, frame.Reader.Reference())); break;
                    case DreamProcOpcode.Pop: frame.Pop(); break;
                    case DreamProcOpcode.NullRef: Set(frame, frame.Reader.Reference(), DmValue.Null); break;
                    case DreamProcOpcode.Assign: case DreamProcOpcode.AssignNoPush: {
                        var reference = frame.Reader.Reference(); var value = frame.Pop(); Set(frame, reference, value);
                        if (opcode == DreamProcOpcode.Assign) frame.Push(value); break;
                    }
                    case DreamProcOpcode.Return: Return(fiber, frame.Pop()); break;
                    case DreamProcOpcode.ReturnFloat: Return(fiber, frame.Reader.Float()); break;
                    case DreamProcOpcode.ReturnReferenceValue: Return(fiber, Get(frame, frame.Reader.Reference())); break;
                    case DreamProcOpcode.OutputReference: { var reference = frame.Reader.Reference(); var value = frame.Pop(); Output(Get(frame, reference), value); break; }
                    case DreamProcOpcode.Output: { var value = frame.Pop(); Output(frame.Pop(), value); break; }
                    case DreamProcOpcode.Call: {
                        var reference = frame.Reader.Reference();
                        if (reference.Kind == 11 && reference.Value >= 0 && reference.Value == Project.NativeMatrixProcId) {
                            var mode = frame.Reader.Byte();
                            var count = frame.Reader.Int();
                            if (!IsMatrixCallShape(mode, count))
                                throw new CompatibilityException("matrix requires zero or six positional finite numbers.");
                            MatrixWork(1 + count); // Reserve bounded argument copies before popping.
                            frame.Push(new DmValue(NewMatrix(Arguments(frame, mode, count))));
                            break;
                        }
                        var args = Arguments(frame);
                        Procedure? proc; DmObject? src = frame.Src;
                        switch (reference.Kind) {
                            case 2: proc = frame.Proc; break;
                            case 6: proc = SuperProc(frame.Proc); break;
                            case 11: proc = Data.Procs[reference.Value]; src = null; break;
                            case 14: proc = src is not null ? ResolveProc(src.TypeId, Project.String(reference.Value)) : null; break;
                            default: throw new CompatibilityException($"Call reference kind {reference.Kind} is not implemented.");
                        }
                        if (proc is null) {
                            if (reference.Kind != 6) throw new CompatibilityException("Call to missing procedure.");
                            frame.Push(DmValue.Null); break;
                        }
                        if (proc.OwningTypeId == 0 && proc.Name == "sleep" && proc.Bytecode is not { Length: > 0 }) {
                            frame.Push(DmValue.Null);
                            var delay = args.Length > 0 ? args[0].Number : 0;
                            if (!float.IsFinite(delay)) throw new CompatibilityException("Non-finite sleep delay.");
                            if (Suspend(fiber, Math.Max(0, delay))) return true;
                        } else if (proc.OwningTypeId == 0 && proc.Name == "typesof" && proc.Bytecode is not { Length: > 0 }) {
                            frame.Push(new(TypesOf(args)));
                        } else if (proc.OwningTypeId == 0 && proc.Name == "fexists" && proc.Bytecode is not { Length: > 0 }) {
                            frame.Push(FileExists(args.Length == 0 ? DmValue.Null : args[0]));
                        } else if (proc.OwningTypeId == 0 && proc.Name == "file2text" && proc.Bytecode is not { Length: > 0 }) {
                            frame.Push(FileText(args.Length == 0 ? DmValue.Null : args[0]));
                        } else if (proc.OwningTypeId == 0 && proc.Name == "splittext" && proc.Bytecode is not { Length: > 0 }) {
                            frame.Push(new DmValue(SplitText(args)));
                        } else {
                            if (proc.Bytecode is not { Length: > 0 } && !CanExecuteEmpty(proc))
                                throw new CompatibilityException($"Native proc '{proc.Name}' is not implemented.");
                            if (fiber.Frames.Count >= 512) throw new CompatibilityException("Procedure call depth limit exceeded.");
                            fiber.Frames.Add(new Frame(proc, src, args) { Usr = frame.Usr });
                        }
                        break;
                    }
                    case DreamProcOpcode.DereferenceCall: {
                        var name = Project.String(frame.Reader.Int());
                        var args = Arguments(frame);
                        if (frame.Pop().Value is not DmObject instance)
                            throw new CompatibilityException($"Cannot call method '{name}' on a null or non-object receiver.");
                        var proc = ResolveProc(instance.TypeId, name)
                            ?? throw new CompatibilityException($"Type {instance.TypePath} has no procedure '{name}'.");
                        if (proc.Bytecode is not { Length: > 0 } && !CanExecuteEmpty(proc))
                            throw new CompatibilityException($"Native proc '{proc.Name}' is not implemented.");
                        PushFrame(fiber, new Frame(proc, instance, args) { Usr = frame.Usr });
                        break;
                    }

                    case DreamProcOpcode.CreateListEnumerator: {
                        var id = ReadEnumeratorId(frame);
                        frame.Enumerators[id] = CreateListSnapshot(frame.Pop());
                        break;
                    }
                    case DreamProcOpcode.Enumerate: {
                        var id = ReadEnumeratorId(frame);
                        var reference = frame.Reader.Reference();
                        var failure = frame.Reader.Int();
                        var enumerator = frame.Enumerators[id];
                        if (enumerator is null) { frame.Reader.Position = failure; break; }
                        var success = enumerator.Next(out var value);
                        Set(frame, reference, value); // Exhaustion assigns null before jumping.
                        if (!success) frame.Reader.Position = failure;
                        break;
                    }
                    case DreamProcOpcode.EnumerateNoAssign: {
                        var id = ReadEnumeratorId(frame);
                        var failure = frame.Reader.Int();
                        if (frame.Enumerators[id] is not { } enumerator || !enumerator.Next(out _))
                            frame.Reader.Position = failure;
                        break;
                    }
                    case DreamProcOpcode.DestroyEnumerator:
                        frame.Enumerators[ReadEnumeratorId(frame)] = null;
                        break;
                    case DreamProcOpcode.LocateCoord: {
                        var z = frame.Pop(); var y = frame.Pop(); var x = frame.Pop();
                        frame.Push(LocateCoordinates(x, y, z));
                        break;
                    }
                    case DreamProcOpcode.CreateObjectDirect: {
                        var typeId = frame.Reader.Int();
                        BeginConstruction(fiber, frame, typeId, Arguments(frame));
                        break;
                    }
                    case DreamProcOpcode.CreateObject: {
                        var mode = frame.Reader.Byte(); var count = frame.Reader.Int();
                        var type = frame.Pop(); var overrides = frame.Pop();
                        if (!overrides.IsNull) throw new CompatibilityException("Construction variable overrides are not implemented.");
                        if (type.Value is not DmTypeReference reference)
                            throw new CompatibilityException("Dynamic construction requires a type reference.");
                        BeginConstruction(fiber, frame, reference.Id, Arguments(frame, mode, count));
                        break;
                    }
                    case DreamProcOpcode.Spawn: {
                        var jump = frame.Reader.Int(); var delay = frame.Pop().Number;
                        if (!float.IsFinite(delay)) throw new CompatibilityException("Non-finite spawn delay.");
                        var spawned = new Fiber(frame.Clone());
                        frame.Reader.Position = jump;
                        if (delay < 0) {
                            if (_immediateSpawnDepth >= 128) throw new CompatibilityException("Immediate spawn depth limit exceeded.");
                            fiber.ImmediateChild = spawned;
                            fiber.ImmediateSpawnOffset = offset;
                        } else Enqueue(spawned, Time + delay);
                        break;
                    }
                    case DreamProcOpcode.Jump: frame.Reader.Position = frame.Reader.Int(); break;
                    case DreamProcOpcode.JumpIfFalse: case DreamProcOpcode.JumpIfTrue: {
                        var jump = frame.Reader.Int(); var truth = frame.Pop().IsTruthy;
                        if (truth == (opcode == DreamProcOpcode.JumpIfTrue)) frame.Reader.Position = jump; break;
                    }
                    case DreamProcOpcode.BooleanNot: frame.Push(frame.Pop().IsTruthy ? 0 : 1); break;
                    case DreamProcOpcode.Negate: frame.Push(-frame.Pop().Number); break;
                    case DreamProcOpcode.Add: { var right = frame.Pop(); var left = frame.Pop(); frame.Push(Add(left, right)); break; }
                    case DreamProcOpcode.Subtract: { var right = frame.Pop(); var left = frame.Pop(); frame.Push(Subtract(left, right)); break; }
                    case DreamProcOpcode.Multiply: Binary(frame, (a, b) => a * b); break;
                    case DreamProcOpcode.Divide: Binary(frame, (a, b) => b == 0 ? throw new CompatibilityException("Division by zero.") : a / b); break;
                    case DreamProcOpcode.Modulus: Binary(frame, IntegerModulus); break;
                    case DreamProcOpcode.CompareEquals: case DreamProcOpcode.CompareNotEquals: {
                        var equal = frame.Pop().Equals(frame.Pop()); frame.Push(equal == (opcode == DreamProcOpcode.CompareEquals) ? 1 : 0); break;
                    }
                    case DreamProcOpcode.CompareLessThan: Compare(frame, (a, b) => a < b); break;
                    case DreamProcOpcode.CompareLessThanOrEqual: Compare(frame, (a, b) => a <= b); break;
                    case DreamProcOpcode.CompareGreaterThan: Compare(frame, (a, b) => a > b); break;
                    case DreamProcOpcode.CompareGreaterThanOrEqual: Compare(frame, (a, b) => a >= b); break;
                    case DreamProcOpcode.Append: case DreamProcOpcode.AppendNoPush: {
                        var reference = frame.Reader.Reference();
                        if (reference.Kind is 7 or 12) throw new CompatibilityException("Compound indexed/field assignment is not implemented.");
                        var right = frame.Pop(); var value = Add(Get(frame, reference), right); Set(frame, reference, value);
                        if (opcode == DreamProcOpcode.Append) frame.Push(value); break;
                    }
                    case DreamProcOpcode.FormatString: {
                        var format = Project.String(frame.Reader.Int()); var values = frame.PopMany(frame.Reader.Int()); var result = new StringBuilder(); var index = 0;
                        foreach (var ch in format) {
                            if (!StringFormatEncoder.Decode(ch, out var suffix)) result.Append(ch);
                            else if (suffix is StringFormatEncoder.FormatSuffix.StringifyWithArticle or StringFormatEncoder.FormatSuffix.StringifyNoArticle) {
                                if (index >= values.Length) throw new CompatibilityException("Missing interpolation value.");
                                if (values[index].Value is DmObject) throw new CompatibilityException("Object/article string interpolation is not implemented.");
                                result.Append(FormatValue(values[index++]));
                            } else throw new CompatibilityException($"String format {suffix} is not implemented.");
                        }
                        if (index != values.Length) throw new CompatibilityException("Unused interpolation values.");
                        frame.Push(result.ToString()); break;
                    }
                    case DreamProcOpcode.CreateList: {
                        var count = frame.Reader.Int();
                        if (count < 0 || count > frame.Stack.Count) throw new CompatibilityException("Invalid list value count.");
                        ChargeListAllocation(1L + count);
                        var values = frame.PopMany(count);
                        var list = new DmList(); list.Values.AddRange(values); frame.Push(new(list)); break;
                    }
                    case DreamProcOpcode.CreateStrictAssociativeList: {
                        var count = frame.Reader.Int();
                        if (count < 0 || count > frame.Stack.Count / 2)
                            throw new CompatibilityException("Invalid strict associative list pair count.");
                        ChargeListAllocation(1L + count * 2L);
                        var pairs = frame.PopMany(count * 2);
                        var list = new DmAssocList();
                        for (var i = 0; i < pairs.Length; i += 2) list.Set(pairs[i], pairs[i + 1]);
                        frame.Push(new(list)); break;
                    }
                    case DreamProcOpcode.CreateAssociativeList: {
                        var count = frame.Reader.Int();
                        if (count < 0 || count > frame.Stack.Count / 2)
                            throw new CompatibilityException("Invalid associative list pair count.");
                        ChargeListAllocation(1L + count * 2L);
                        var pairs = frame.PopMany(count * 2);
                        var list = new DmList();
                        for (var i = 0; i < pairs.Length; i += 2) {
                            var key = pairs[i]; var value = pairs[i + 1];
                            if (key.IsNull) list.Values.Add(value);
                            else if (key.Value is float number) {
                                var index = BoundedListInteger(number);
                                if (index == list.Values.Count + 1) list.Values.Add(value);
                                else list.Set(index, value);
                            } else list.Set(key, value);
                        }
                        frame.Push(new(list)); break;
                    }
                    case DreamProcOpcode.CreateMultidimensionalList: {
                        var count = frame.Reader.Int();
                        if (count < 0 || count > 32) throw new CompatibilityException("List dimension count exceeds the supported limit.");
                        frame.Push(new(CreateDimensionedList(frame.PopMany(count)))); break;
                    }
                    case DreamProcOpcode.CreateListNFloats: case DreamProcOpcode.CreateListNStrings:
                    case DreamProcOpcode.CreateListNResources: case DreamProcOpcode.CreateListNRefs: {
                        var count = frame.Reader.Int();
                        if (count < 0) throw new CompatibilityException("Invalid literal list length.");
                        ChargeListAllocation(1L + count);
                        var list = new DmList();
                        for (var i = 0; i < count; i++) list.Values.Add(opcode switch {
                            DreamProcOpcode.CreateListNFloats => new(frame.Reader.Float()),
                            DreamProcOpcode.CreateListNStrings => new(Project.String(frame.Reader.Int())),
                            DreamProcOpcode.CreateListNResources => new(new DmResource(Project.String(frame.Reader.Int()))),
                            _ => Get(frame, frame.Reader.Reference())
                        });
                        frame.Push(new(list)); break;
                    }
                    case DreamProcOpcode.DereferenceIndex: { var index = frame.Pop(); frame.Push(ReadIndex(frame.Pop(), index)); break; }
                    case DreamProcOpcode.Initial: {
                        var key = frame.Pop(); var owner = frame.Pop();
                        frame.Push(InitialValue(owner, key)); break;
                    }
                    case DreamProcOpcode.DereferenceField: { var name = Project.String(frame.Reader.Int()); frame.Push(Field(frame.Pop(), name)); break; }
                    case DreamProcOpcode.PushRefAndDereferenceField: { var value = Get(frame, frame.Reader.Reference()); frame.Push(Field(value, Project.String(frame.Reader.Int()))); break; }
                    case DreamProcOpcode.IsNull: frame.Push(frame.Pop().IsNull ? 1 : 0); break;
                    case DreamProcOpcode.Length: { var value = frame.Pop().Value; frame.Push(value switch { string text => text.Length, DmList list => list.Values.Count, DmAssocList list => list.Count, null => 0, _ => throw new CompatibilityException("length() on this value is not implemented.") }); break; }
                    case DreamProcOpcode.PushNRefs: case DreamProcOpcode.PushNFloats: case DreamProcOpcode.PushNStrings: {
                        var count = frame.Reader.Int(); for (var i = 0; i < count; i++) frame.Push(opcode switch {
                            DreamProcOpcode.PushNRefs => Get(frame, frame.Reader.Reference()),
                            DreamProcOpcode.PushNFloats => new(frame.Reader.Float()), _ => new(Project.String(frame.Reader.Int())) }); break;
                    }
                    case DreamProcOpcode.PushNOfStringFloats: {
                        var count = frame.Reader.Int();
                        if (count < 0 || count > (frame.Reader.Length - frame.Reader.Position) / 8)
                            throw new CompatibilityException("Invalid string/float literal count.");
                        for (var i = 0; i < count; i++) {
                            frame.Push(Project.String(frame.Reader.Int())); frame.Push(frame.Reader.Float());
                        }
                        break;
                    }
                    case DreamProcOpcode.PushStringFloat: frame.Push(Project.String(frame.Reader.Int())); frame.Push(frame.Reader.Float()); break;
                    default: throw new CompatibilityException($"Opcode {opcode} (0x{(byte)opcode:X2}) is not implemented.");
                }
            } catch (CompatibilityException ex) { throw Fault(frame, offset, ex.Message, ex); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException or KeyNotFoundException) { throw Fault(frame, offset, $"Malformed or unsupported execution: {ex.Message}", ex); }
        }
        return true;
    }

    private static int ReadEnumeratorId(Frame frame) {
        var id = frame.Reader.Int();
        if (id < 0 || id >= 16)
            throw new CompatibilityException("List enumerator slot must be in 0..15.");
        return id;
    }

    private ListSnapshot CreateListSnapshot(DmValue container) {
        void Work(int count) {
            if (count < 0 || count > _budget)
                throw new CompatibilityException("Instruction budget exhausted during list enumeration snapshot.");
            _budget -= count;
        }
        DmList? list = null;
        switch (container.Value) {
            case DmList ordinary: list = ordinary; break;
            case DmAssocList:
                throw new CompatibilityException("Strict associative list enumeration is not implemented.");
            case DmObject instance:
                if (instance.TypeId < 0 || instance.TypeId >= Data.Types.Length)
                    throw new CompatibilityException("List enumeration has invalid object type identity.");
                for (int? id = instance.TypeId; id.HasValue; id = Data.Types[id.Value].Parent) {
                    Work(1);
                    // Fixed-length ordinal comparisons bound path work even for long metadata paths.
                    var path = Data.Types[id.Value].Path;
                    if (string.Equals(path, "/atom", StringComparison.Ordinal) ||
                        string.Equals(path, "/world", StringComparison.Ordinal))
                        throw new CompatibilityException("Native atom/world contents enumeration is not implemented.");
                    if (string.Equals(path, "/list", StringComparison.Ordinal) ||
                        string.Equals(path, "/alist", StringComparison.Ordinal))
                        throw new CompatibilityException("Native list object representation enumeration is not implemented.");
                }
                break; // Ordinary datum containers enumerate no values in the pin.
            case null or float or string or DmTypeReference or DmResource: break;
            default: throw new CompatibilityException("List enumeration operand representation is not implemented.");
        }
        var count = list?.Values.Count ?? 0;
        Work(count);
        if (list is not null) {
            foreach (var value in list.Values) {
                if (value.Value is not (null or float or string or DmTypeReference or DmResource or DmObject or DmList or DmAssocList))
                    throw new CompatibilityException("List enumeration value representation is not implemented.");
            }
        }
        Work(count);
        ChargeListAllocation(1L + count);
        // Only unfiltered key iteration is supported. Associative enumeration is
        // still rejected, so no association-value snapshot is exposed here.
        return new ListSnapshot(list?.Values.ToArray() ?? []);
    }

    private DmValue Subtract(DmValue left, DmValue right) {
        if (left.Value is DmAssocList)
            throw new CompatibilityException("Strict associative list subtraction is not implemented.");
        if (left.Value is not DmList source) return left.Number - right.Number;
        if (right.Value is not (null or float or string or DmTypeReference or DmResource or DmObject or DmList or DmAssocList))
            throw new CompatibilityException("List subtraction operand representation is not implemented.");
        void Work(int count) {
            if (count < 0 || count > _budget)
                throw new CompatibilityException("Instruction budget exhausted during list subtraction.");
            _budget -= count;
        }
        // A local comparer meters both hashing and every collision comparison.
        // Detach in finally: returned dictionaries must never retain this runtime.
        var comparer = new SubtractionComparer(Work);
        try {
            foreach (var value in source.Values) comparer.Validate(value);
            if (right.Value is DmList rhs)
                foreach (var value in rhs.Values) comparer.Validate(value);
            else comparer.Validate(right);
            Dictionary<DmValue, DmValue>? associations = null;
            if (source.Values.Count != 0 && source.Associations.Count != 0) {
                // EnsureCapacity(0) only reads capacity. Charge the entire slot array
                // before enumeration, including tombstones from removed host entries.
                Work(source.Associations.EnsureCapacity(0));
                foreach (var pair in source.Associations) comparer.Validate(pair.Key);
                // Scratch storage is separately bounded; orphan entries do not consume
                // the observable output-list allocation allowance.
                if (1L + 2L * source.Associations.Count > ListAllocationLimit)
                    throw new CompatibilityException("List subtraction scratch allocation limit exceeded.");
                Work(source.Associations.EnsureCapacity(0));
                Work(source.Associations.Count);
                associations = new Dictionary<DmValue, DmValue>(source.Associations.Count, comparer);
                foreach (var pair in source.Associations) {
                    // Different hashes can occupy one bucket without invoking Equals.
                    // Count bounds all bucket links, independently of hash distribution.
                    Work(associations.Count);
                    associations.Add(pair.Key, pair.Value);
                }
            }
            Work(source.Values.Count);
            ChargeListAllocation(1L + source.Values.Count);
            var result = new DmList(comparer);
            result.Values.AddRange(source.Values);
            if (associations is not null) {
                foreach (var key in source.Values) {
                    Work(1);
                    Work(associations.Count);
                    if (!associations.TryGetValue(key, out var value)) continue;
                    Work(result.Associations.Count);
                    if (!result.Associations.ContainsKey(key)) {
                        ChargeListAllocation(2);
                        Work(result.Associations.Count);
                        // Meter copying old entries when the output dictionary grows.
                        // Geometric capacity growth has bounded amortized storage cost.
                        if (result.Associations.Count == result.Associations.EnsureCapacity(0))
                            Work(result.Associations.Count);
                        result.Associations.Add(key, value);
                    }
                }
            }
            void RemoveOne(DmValue value) {
                Work(1);
                for (var index = result.Values.Count - 1; index >= 0; index--) {
                    if (!comparer.Equals(result.Values[index], value)) continue;
                    Work(result.Values.Count - index - 1);
                    Work(result.Associations.Count);
                    result.Associations.Remove(value);
                    result.Values.RemoveAt(index);
                    return;
                }
            }
            if (right.Value is DmList removals) {
                foreach (var value in removals.Values) RemoveOne(value);
            } else RemoveOne(right);
            return new(result);
        } finally {
            comparer.Detach();
        }
    }

    private sealed class SubtractionComparer(Action<int> work) : IEqualityComparer<DmValue> {
        private Action<int>? _work = work;
        public void Detach() => _work = null;
        public void Validate(DmValue value) {
            _work?.Invoke(1);
            if (value.Value is not (null or float or string or DmTypeReference or DmResource or DmObject or DmList or DmAssocList))
                throw new CompatibilityException("List subtraction value representation is not implemented.");
        }
        private static string? Text(DmValue value) => value.Value switch {
            string text => text, DmResource resource => resource.Path,
            DmTypeReference type => type.Path, _ => null
        };
        public bool Equals(DmValue left, DmValue right) {
            // Once detached retain precisely the public default dictionary behavior.
            if (_work is null) return left.Equals(right);
            Validate(left); Validate(right);
            if (left.Value?.GetType() == right.Value?.GetType() &&
                !(left.Value is DmTypeReference l && right.Value is DmTypeReference r && l.Id != r.Id)) {
                var a = Text(left); var b = Text(right);
                if (a is not null && b is not null && a.Length == b.Length) _work(a.Length);
            }
            return left.Equals(right);
        }
        public int GetHashCode(DmValue value) {
            if (_work is not null) {
                Validate(value);
                if (Text(value) is { } text) _work(text.Length);
            }
            return value.GetHashCode();
        }
    }

    private DmList TypesOf(DmValue[] arguments) {
        void Work(int count = 1) {
            if (count < 0 || count > _budget)
                throw new CompatibilityException("Instruction budget exhausted during typesof.");
            _budget -= count;
        }
        var roots = new List<int>();
        foreach (var argument in arguments) {
            Work();
            int? root = null;
            switch (argument.Value) {
                case DmTypeReference type: root = type.Id; break;
                case DmObject instance: root = instance.TypeId; break;
                case DmAssocList:
                    throw new CompatibilityException("typesof strict associative list identity is not implemented.");
                case string path:
                    // Bound input processing in UTF-16 code units before suffix checks.
                    // Both suffixes are fixed five-code-unit ordinal comparisons.
                    Work(path.Length);
                    Work(10);
                    if (path.EndsWith("/proc", StringComparison.Ordinal) || path.EndsWith("/verb", StringComparison.Ordinal))
                        throw new CompatibilityException("typesof proc/verb paths are not implemented.");
                    // Pinned ObjectTree's exact path dictionary excludes root ID 0.
                    for (var id = 1; id < Data.Types.Length; id++) {
                        Work();
                        var candidate = Data.Types[id].Path;
                        if (candidate.Length != path.Length) continue;
                        // Length access is constant work; equality can inspect every
                        // code unit, including a long shared prefix. Debit first.
                        Work(candidate.Length);
                        if (string.Equals(candidate, path, StringComparison.Ordinal)) { root = id; break; }
                    }
                    if (root is null) throw new CompatibilityException($"typesof unknown type path (UTF-16 length {path.Length}).");
                    break;
                case null or float or DmResource or DmList: break;
                default: throw new CompatibilityException("typesof argument representation is not implemented.");
            }
            if (root is int typeId) {
                if (typeId < 0 || typeId >= Data.Types.Length)
                    throw new CompatibilityException("typesof invalid type identity.");
                roots.Add(typeId);
            }
        }
        ChargeListAllocation(1);
        var result = new DmList();
        if (roots.Count == 0) return result;
        // Cold and warm calls pay the same admission charge before using the index.
        Work(Data.Types.Length);
        TypeChildIndex? children;
        do {
            children = Project.AdvanceTypeChildIndex();
        } while (children is null);
        var pending = new Stack<int>();
        foreach (var root in roots) {
            pending.Push(root);
            while (pending.TryPop(out var id)) {
                Work();
                ChargeListAllocation(1);
                result.Values.Add(new(new DmTypeReference(id, Data.Types[id].Path)));
                // Visit the child first. A root's siblings belong to other subtrees.
                var sibling = children.NextSibling(id);
                if (id != root && sibling >= 0) pending.Push(sibling);
                var child = children.FirstChild(id);
                if (child >= 0) pending.Push(child);
            }
        }
        return result;
    }

    private void ChargeListAllocation(long count) {
        if (count < 0 || count > _listAllocationRemaining)
            throw new CompatibilityException("List allocation budget exhausted.");
        _listAllocationRemaining -= (int)count;
    }

    private static int BoundedListInteger(float value) {
        var truncated = Math.Truncate((double)value);
        if (!double.IsFinite(truncated) || truncated < int.MinValue || truncated > int.MaxValue)
            throw new CompatibilityException("Non-finite or out-of-range list size/index is not implemented.");
        return (int)truncated;
    }

    private DmList CreateDimensionedList(DmValue[] dimensions) {
        var sizes = dimensions.Select(value => value.Value is float number ? BoundedListInteger(number) : 0).ToArray();
        if (sizes.Length == 1 && sizes[0] < 0)
            throw new CompatibilityException("Negative single-dimension list size is not implemented.");
        for (var i = 0; i < sizes.Length; i++) sizes[i] = Math.Max(0, sizes[i]);
        // Preflight the entire graph before allocation. Count each list and value slot,
        // including empty child lists; zero dimensions still constructs the root list.
        long cost = 1, parents = 1;
        for (var i = 0; i < sizes.Length; i++) {
            var entries = parents * sizes[i];
            cost += entries * (i < sizes.Length - 1 ? 2 : 1);
            if (cost > _listAllocationRemaining) throw new CompatibilityException("List allocation budget exhausted.");
            parents = entries;
        }
        ChargeListAllocation(cost);
        DmList Build(int dimension) {
            var list = new DmList();
            if (dimension == sizes.Length) return list;
            for (var i = 0; i < sizes[dimension]; i++)
                list.Values.Add(dimension + 1 == sizes.Length ? DmValue.Null : new DmValue(Build(dimension + 1)));
            return list;
        }
        return Build(0);
    }

    private DmValue LocateCoordinates(DmValue x, DmValue y, DmValue z) {
        // The pinned handler coerces non-numbers to zero, then truncates each float.
        // Check finite/range before casting so host float-to-int overflow is irrelevant.
        static int Coordinate(DmValue value, int limit) {
            var number = value.Value is float n ? n : 0;
            var truncated = Math.Truncate((double)number);
            if (!double.IsFinite(truncated) || truncated < int.MinValue || truncated > int.MaxValue)
                throw new CompatibilityException("Locate coordinates outside the finite signed 32-bit integer range are not implemented.");
            return truncated >= 1 && truncated <= limit ? (int)truncated : 0;
        }
        var position = (Coordinate(x, Width), Coordinate(y, Height), Coordinate(z, Levels));
        return _mapTurfs.TryGetValue(position, out var turf) ? new(turf) : DmValue.Null;
    }

    private static DmValue Add(DmValue left, DmValue right) {
        if (left.Value is string && right.Value is string) return left.ToString() + right.ToString();
        return left.Number + right.Number;
    }
    private static float IntegerModulus(float left, float right) {
        // DM % uses integer operands. Check the coerced divisor, not the original float.
        var numerator = Math.Truncate((double)left);
        var denominator = Math.Truncate((double)right);
        if (!double.IsFinite(numerator) || !double.IsFinite(denominator) ||
            numerator < int.MinValue || numerator > int.MaxValue || denominator < int.MinValue || denominator > int.MaxValue)
            throw new CompatibilityException("Modulo operands outside the signed 32-bit integer range are not implemented.");
        if (denominator == 0) throw new CompatibilityException("Modulo divisor becomes zero after integer coercion.");
        // Widen first: int.MinValue % -1 must not trigger a host-language overflow exception.
        return (float)((long)numerator % (long)denominator);
    }
    private static void Binary(Frame f, Func<float, float, float> op) { var b = f.Pop().Number; f.Push(op(f.Pop().Number, b)); }
    private static void Compare(Frame f, Func<float, float, bool> op) { var b = f.Pop().Number; f.Push(op(f.Pop().Number, b) ? 1 : 0); }
    private void Output(DmValue recipient, DmValue value) {
        if (!ReferenceEquals(recipient.Value, World)) throw new CompatibilityException("Only world output is implemented.");
        if (value.Value is DmObject obj && _mapLocations.ContainsKey(obj))
            throw new CompatibilityException("Map atom stringification is not implemented.");
        _logs.Add(new(Time, FormatValue(value)));
    }
    private DmValue[] Arguments(Frame frame) {
        var kind = frame.Reader.Byte(); var count = frame.Reader.Int();
        return Arguments(frame, kind, count);
    }
    private static DmValue[] Arguments(Frame frame, byte kind, int count) {
        return kind switch {
            0 when count == 0 => [], 1 => frame.PopMany(count), 4 when count == 0 => [.. frame.Arguments],
            _ => throw new CompatibilityException($"Call argument mode {kind} is not implemented.")
        };
    }

    private DmValue InitialValue(DmValue owner, DmValue key) {
        if (key.Value is float)
            throw new CompatibilityException("initial numeric indexing is not implemented.");
        if (key.Value is not string name)
            throw new CompatibilityException("initial requires a string field name.");
        if (owner.Value is null or float or string) return DmValue.Null;
        var typeId = owner.Value switch {
            DmTypeReference type => type.Id,
            DmObject instance => instance.TypeId,
            _ => throw new CompatibilityException("initial requires a type or object owner; list/primitive forms are not implemented.")
        };
        if (typeId < 0 || typeId >= Data.Types.Length)
            throw new CompatibilityException("initial has invalid type identity.");
        void Work(int count) {
            if (count < 0 || count > _budget)
                throw new CompatibilityException("Instruction budget exhausted during initial ancestry lookup.");
            _budget -= count;
        }
        // Explicit scans bound hash collisions and equal-prefix comparisons.
        bool SameName(string candidate) {
            Work(1);
            if (candidate.Length != name.Length) return false;
            Work(name.Length);
            return string.Equals(candidate, name, StringComparison.Ordinal);
        }
        bool ContainsName(IEnumerable<string> names) {
            foreach (var candidate in names) if (SameName(candidate)) return true;
            return false;
        }
        Work(name.Length);
        JsonElement? declared = null;
        var declarationHasInitializer = false;
        var hasStatic = false;
        var atom = false; var movable = false; var mob = false; var world = false; var native = false;
        for (int? current = typeId; current is not null; current = Data.Types[current.Value].Parent) {
            Work(1);
            var type = Data.Types[current.Value];
            if (declared is null && type.Variables is { } variables) {
                foreach (var field in variables) {
                    if (!SameName(field.Key)) continue;
                    declared = field.Value;
                    declarationHasInitializer = type.InitProc is int initializer &&
                        Data.Procs[initializer].Bytecode is { Length: > 0 };
                    break;
                }
            }
            // Root global mappings are not inherited by upstream object definitions.
            if (!hasStatic && (current.Value != 0 || typeId == 0) && type.GlobalVariables is { } globals)
                hasStatic = ContainsName(globals.Keys);
            atom |= type.Path == "/atom"; movable |= type.Path == "/atom/movable";
            mob |= type.Path == "/mob"; world |= type.Path == "/world";
            native |= type.Path is "/client" or "/list" or "/alist" or "/savefile" or "/icon" or
                "/matrix" or "/regex" or "/dm_filter" or "/pixloc" or "/sound" or "/image" or "/mutable_appearance" or
                "/generator" or "/particles" or "/database" or "/database/query" or
                "/vector" or "/callee" or "/exception";
        }
        if (native || name is "type" or "parent_type" or "vars" or "tag" ||
            (atom && (ContainsName(UnsupportedAtomFields) || name is "loc" or "x" or "y" or "z")) ||
            (movable && ContainsName(UnsupportedMovableFields)) || (mob && ContainsName(UnsupportedMobFields)) ||
            (world && ContainsName(BuiltinWorldFields)))
            throw new CompatibilityException("initial native/metadata field is not implemented.");
        // Original instance Variables take precedence over any static mapping,
        // even when that mapping is declared on a more-derived type.
        if (declared is JsonElement value) {
            // The compiler serializes executable defaults as null on their declaring
            // type. Do not confuse that placeholder with a proven constant null.
            if (value.ValueKind == JsonValueKind.Null && declarationHasInitializer)
                throw new CompatibilityException("initial null default has ambiguous executable initializer provenance.");
            // Raw-span length requires no temporary text allocation.
            Work(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value).Length);
            var result = ConvertValue(value);
            if (result.Value is not (null or float or string or DmTypeReference or DmResource))
                throw new CompatibilityException("initial mutable default is not implemented.");
            return result;
        }
        if (hasStatic)
            throw new CompatibilityException("initial static/global field is not implemented.");
        return DmValue.Null;
    }

    private DmValue Field(DmValue owner, string name) {
        if (owner.Value is DmList list && name == "len") return list.Values.Count;
        if (owner.Value is DmAssocList associative) {
            if (name == "len") return associative.Count;
            throw new CompatibilityException($"alist.{name} is not implemented.");
        }
        if (owner.Value is not DmObject obj) throw new CompatibilityException($"Cannot read field '{name}' on this value.");
        if (name == "transform" && HasNativeTransform(obj)) return new DmValue(ReadTransform(obj));
        if (ReferenceEquals(obj, World)) {
            if (BuiltinWorldFields.Contains(name) && !ReadableWorldFields.Contains(name))
                throw new CompatibilityException($"world.{name} is not implemented.");
            if (name == "time") return (float)Time;
        }
        if (_mapLocations.TryGetValue(obj, out var location)) {
            if (location.MovableSnapshot is { } snapshot) {
                switch (name) {
                    case "dir": return snapshot.Direction;
                    case "pixel_x": return snapshot.PixelX;
                    case "pixel_y": return snapshot.PixelY;
                }
            }
            // The pinned appearance setter retains strings and normalizes every
            // nonstring name to empty. Keep raw definition values untouched.
            if (IsSubtypeOf(obj.TypeId, "/turf")) {
                if (name == "name") return obj.GetVariableOrDefault("name").Value as string ?? "";
                if (name == "layer") return TurfLayer(obj);
            }
            if (IsSubtypeOf(obj.TypeId, "/atom/movable")) {
                if (name == "name") {
                    var raw = obj.GetVariableOrDefault(name);
                    // Upstream fills null type-definition names before map overrides.
                    // Explicit null map names instead pass through the native setter.
                    if (raw.IsNull && !location.NameOverridden)
                        return obj.TypePath[(obj.TypePath.LastIndexOf('/') + 1)..].Replace('_', ' ');
                    return raw.Value as string ?? "";
                }
                if (name == "desc") {
                    ValidateMapAppearanceOwner(obj, name, writing: false);
                    var desc = obj.GetVariableOrDefault(name);
                    return desc.Value is null or string ? desc : throw new CompatibilityException("Map desc requires text or null.");
                }
                if (name == "invisibility") {
                    ValidateMapAppearanceOwner(obj, name, writing: false);
                    return NormalizeMapInvisibility(obj.GetVariableOrDefault(name), declared: !location.InvisibilityOverridden);
                }
                if (name == "icon" && _mapIcons is not null) return ReadMapIcon(obj);
                if (name == "icon_state")
                    return obj.GetVariableOrDefault(name).Value is string state ? new(state) : DmValue.Null;
            }
            if (UnsupportedMapField(obj, name)) throw new CompatibilityException($"Map atom.{name} is not implemented.");
            if (_multiLevelAreas.Contains(obj) && name is ("x" or "y" or "z"))
                throw new CompatibilityException("Coordinates for multi-level map areas are not implemented.");
            switch (name) {
                case "x": return location.X;
                case "y": return location.Y;
                case "z": return location.Z;
                case "loc": return new(location.Loc);
            }
        }
        if (name == "vars") throw new CompatibilityException("datum.vars is not implemented.");
        return obj.TryGetVariable(name, out var value) ? value : throw new CompatibilityException($"Unknown field '{name}'.");
    }
    private DmValue Get(Frame frame, Reference reference) => reference.Kind switch {
        1 => new(frame.Src), 2 => frame.Result, 3 => frame.Usr, 5 => new(World),
        8 => reference.Value < frame.Arguments.Length ? frame.Arguments[reference.Value] : DmValue.Null,
        9 => frame.Locals[reference.Value], 10 => _globals[reference.Value],
        12 => Field(frame.Pop(), Project.String(reference.Value)),
        13 => Field(new(frame.Src), Project.String(reference.Value)),
        7 => Index(frame),
        _ => throw new CompatibilityException($"Read reference kind {reference.Kind} is not implemented.")
    };
    private static DmValue ReadIndex(DmValue owner, DmValue index) => owner.Value switch {
        DmList list => list.Get(index),
        DmAssocList list => list.Get(index),
        _ => throw new CompatibilityException("Only list indexing is implemented.")
    };
    private static DmValue Index(Frame frame) { var index = frame.Pop(); return ReadIndex(frame.Pop(), index); }
    private void Set(Frame frame, Reference reference, DmValue value) {
        switch (reference.Kind) {
            case 2: frame.Result = value; break;
            case 8: if (reference.Value >= frame.Arguments.Length) Array.Resize(ref frame.Arguments, reference.Value + 1); frame.Arguments[reference.Value] = value; break;
            case 9: frame.Locals[reference.Value] = value; break;
            case 10: _globals[reference.Value] = value; break;
            case 12: SetField(frame.Pop(), Project.String(reference.Value), value); break;
            case 13: SetField(new(frame.Src), Project.String(reference.Value), value); break;
            case 7: {
                var index = frame.Pop();
                switch (frame.Pop().Value) {
                    case DmList list: list.Set(index, value); break;
                    case DmAssocList list:
                        if (!list.ContainsKey(index)) ChargeListAllocation(2);
                        list.Set(index, value);
                        break;
                    default: throw new CompatibilityException("Only list indexing is implemented.");
                }
                break;
            }
            default: throw new CompatibilityException($"Write reference kind {reference.Kind} is not implemented.");
        }
    }
    private void SetField(DmValue owner, string name, DmValue value) {
        if (owner.Value is DmAssocList) throw new CompatibilityException($"Mutation of alist.{name} is not implemented.");
        if (owner.Value is not DmObject obj || !obj.HasVariable(name)) throw new CompatibilityException($"Cannot assign field '{name}'.");
        if (name == "transform" && HasNativeTransform(obj)) { WriteTransform(obj, value); return; }
        if (obj.IsNativeMatrix && name is "a" or "b" or "c" or "d" or "e" or "f")
            FiniteCoefficient(value);
        if (_mapLocations.ContainsKey(obj) && ProtectedMapField(obj, name))
            throw new CompatibilityException($"Mutation of map atom.{name} is not implemented.");
        if (ReferenceEquals(obj, World) && BuiltinWorldFields.Contains(name)) throw new CompatibilityException($"Mutation of world.{name} is not implemented.");
        if (name is "type" or "parent_type" or "vars")
            throw new CompatibilityException($"Mutation of datum.{name} is not implemented.");
        obj.SetVariable(name, value);
    }
    private static void Return(Fiber fiber, DmValue value) {
        fiber.Frames.RemoveAt(fiber.Frames.Count - 1);
        if (fiber.Frames.Count > 0 && fiber.Frames[^1].Construction is null) fiber.Frames[^1].Push(value);
    }
    // set waitfor=0 only detaches once execution actually yields, preserving synchronous work before sleep.
    private bool Suspend(Fiber fiber, double delay) {
        var split = fiber.Frames.FindLastIndex(f => f.Construction is null && (f.Proc.Attributes & 32) != 0);
        if (split > 0) {
            var continuation = new Fiber(); continuation.Frames.AddRange(fiber.Frames.Skip(split));
            var result = fiber.Frames[split].Result;
            fiber.Frames.RemoveRange(split, fiber.Frames.Count - split);
            if (fiber.Frames[^1].Construction is null) fiber.Frames[^1].Push(result);
            Enqueue(continuation, Time + delay);
            return false;
        }
        Enqueue(fiber, Time + delay);
        return true;
    }
    private CompatibilityException Fault(Frame frame, int offset, string detail, Exception? inner = null) => new($"{detail} at {Project.Location(frame.Proc, offset)}", inner);

    private sealed class Fiber {
        public Fiber? ImmediateChild;
        public int ImmediateSpawnOffset;
        public List<Frame> Frames { get; } = [];
        public Fiber() { }
        public Fiber(Frame frame) { Frames.Add(frame); }
    }
    private sealed class Construction(DmObject instance, Procedure? initializer, Procedure? constructor) {
        public DmObject Instance { get; } = instance;
        public Procedure? Initializer { get; } = initializer;
        public Procedure? Constructor { get; } = constructor;
        public int Stage;
    }

    private sealed class ListSnapshot(DmValue[] values) {
        private int _position;
        public bool Next(out DmValue value) {
            if (_position < values.Length) { value = values[_position++]; return true; }
            value = DmValue.Null;
            return false;
        }
    }
    private sealed class Frame {
        public Construction? Construction;
        public Procedure Proc { get; }
        public ByteReader Reader { get; }
        public DmObject? Src { get; }
        public DmValue Usr;
        public DmValue Result;
        public DmValue[] Arguments;
        public DmValue[] Locals;
        public List<DmValue> Stack { get; } = [];
        public ListSnapshot?[] Enumerators { get; } = new ListSnapshot?[16];
        public Frame(Procedure proc, DmObject? src, DmValue[] args) {
            Proc = proc; Reader = new(proc.Bytecode ?? []); Src = src; Arguments = [.. args]; Locals = new DmValue[Math.Max(256, proc.MaxVariableId + 1)];
        }
        public void Push(DmValue value) => Stack.Add(value);
        public DmValue Pop() { if (Stack.Count == 0) throw new CompatibilityException("DM evaluation stack underflow."); var result = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); return result; }
        public DmValue[] PopMany(int count) { if (count < 0 || count > Stack.Count) throw new CompatibilityException("Invalid DM stack argument count."); var result = Stack.GetRange(Stack.Count - count, count).ToArray(); Stack.RemoveRange(Stack.Count - count, count); return result; }
        // Pinned spawn copies locals/arguments but starts with empty enumerator slots.
        public Frame Clone() { var frame = new Frame(Proc, Src, Arguments) { Usr = Usr, Result = Result, Locals = [.. Locals] }; frame.Reader.Position = Reader.Position; frame.Stack.AddRange(Stack); return frame; }
    }
}
