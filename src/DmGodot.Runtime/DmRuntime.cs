using System.Text;
using System.Text.Json;
using DMCompiler.Bytecode;

namespace DmGodot.Runtime;

/// <summary>
/// Experimental single-threaded DM execution core. Time is virtual deciseconds.
/// Unsupported operations fail at their DM source location; they are never silently skipped.
/// </summary>
public sealed class DmRuntime {
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
    private sealed record MapLocation(int X, int Y, int Z, DmObject? Loc);
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
    private bool UnsupportedMapField(DmObject obj, string name) =>
        UnsupportedAtomFields.Contains(name) ||
        (IsSubtypeOf(obj.TypeId, "/atom/movable") && UnsupportedMovableFields.Contains(name)) ||
        (IsSubtypeOf(obj.TypeId, "/mob") && UnsupportedMobFields.Contains(name));
    private bool ProtectedMapField(DmObject obj, string name) => UnsupportedMapField(obj, name) ||
        name is "loc" or "x" or "y" or "z";
    private readonly List<RuntimeLog> _logs = [];
    private readonly PriorityQueue<Fiber, (double Time, long Sequence)> _queue = new();
    private readonly DmValue[] _globals;
    private long _sequence;
    private bool _booted;
    private int _budget;
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
    public int InstructionBudget { get; init; } = 100_000;
    private CompiledData Data => Project.Data;

    public DmRuntime(CompiledProject project) {
        Project = project;
        _globals = new DmValue[Data.Globals?.GlobalCount ?? 0];
        foreach (var (id, value) in Data.Globals?.GlobalValues ?? []) {
            if (id < 0 || id >= _globals.Length) throw new CompatibilityException($"Invalid global index {id}.");
            _globals[id] = ConvertValue(value);
        }
        World = CreateInstance(FindType("/world"));
        // Constructor-time initialization has one fixed aggregate budget; the init-only
        // InstructionBudget setting applies to later Boot/InvokeGlobal/AdvanceBy calls.
        _budget = 100_000;
        LoadMaps();
        World.Variables["maxx"] = Width;
        World.Variables["maxy"] = Height;
        World.Variables["maxz"] = Levels;
    }

    public void Boot() {
        if (_booted) throw new InvalidOperationException("The world has already booted.");
        _booted = true;
        _budget = InstructionBudget;
        if (Data.GlobalInitProc is { } init) {
            var fiber = new Fiber(new Frame(init, null, []));
            Execute(fiber);
            if (fiber.Frames.Count > 0) throw new CompatibilityException("A yielding global initializer is not supported.");
        }
        // Each callback is an independent root: execute its prefix now, but do not
        // drain deferred work until all map callbacks and world.New have started.
        var visitedAreas = new HashSet<DmObject>();
        foreach (var cell in _mapCells)
            if (visitedAreas.Add(cell.Area)) StartNew(cell.Area, []);
        if (_defaultArea is not null && visitedAreas.Add(_defaultArea)) StartNew(_defaultArea, []);
        foreach (var cell in _mapCells.OrderBy(c => c.Z).ThenByDescending(c => c.Y).ThenByDescending(c => c.X))
            StartNew(cell.Turf, []);
        foreach (var movable in _mapMovableNewOrder)
            StartNew(movable, [new DmValue(_mapLocations[movable].Loc)]);
        StartNew(World, []);
        AdvanceBy(0);
    }

    private void StartNew(DmObject instance, DmValue[] arguments) {
        var proc = ResolveProc(instance.TypeId, "New");
        if (proc is not null) Execute(new Fiber(new Frame(proc, instance, arguments)));
    }

    public void AdvanceBy(double deciseconds) {
        if (!_booted) throw new InvalidOperationException("Boot the runtime before advancing it.");
        if (!double.IsFinite(deciseconds) || deciseconds < 0) throw new ArgumentOutOfRangeException(nameof(deciseconds));
        var target = Time + deciseconds;
        if (!double.IsFinite(target)) throw new ArgumentOutOfRangeException(nameof(deciseconds));
        _budget = InstructionBudget;
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
        var result = new DmObject(id, Data.Types[id].Path);
        var chain = new Stack<TypeDefinition>();
        for (int? current = id; current is not null; current = Data.Types[current.Value].Parent) chain.Push(Data.Types[current.Value]);
        foreach (var type in chain) {
            if (!initializeLater && type.InitProc is int init && Data.Procs[init].Bytecode is { Length: > 0 })
                throw new CompatibilityException($"Type initializers are not implemented: {type.Path}.");
            foreach (var (name, value) in type.Variables ?? []) result.Variables[name] = ConvertValue(value);
        }
        result.Variables["type"] = new(new DmTypeReference(id, result.TypePath));
        result.Variables["parent_type"] = Data.Types[id].Parent is int parent
            ? new(new DmTypeReference(parent, Data.Types[parent].Path)) : DmValue.Null;
        return result;
    }
    private DmValue ConvertValue(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Null => DmValue.Null,
        JsonValueKind.Number => new(value.GetSingle()),
        JsonValueKind.String => new(value.GetString()),
        JsonValueKind.Object => ConvertTaggedValue(value),
        _ => throw new CompatibilityException($"Unsupported compiler value: {value.ValueKind}.")
    };
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
        if ((Data.Maps ?? []).Any(map => map.MaxZ > 0)) {
            var defaultAreaId = World.Variables.GetValueOrDefault("area").Value is DmTypeReference defaultAreaType
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
                    var x = block.X + i % block.Width;
                    var y = block.Y + block.Height - 1 - i / block.Width;
                    if (x < 1 || y < 1 || block.Z < 1 || x > map.MaxX || y > map.MaxY || block.Z > map.MaxZ)
                        throw new CompatibilityException("Map block extends outside its declared dimensions.");
                    if (!map.CellDefinitions.TryGetValue(block.Cells[i], out var cell)) throw new CompatibilityException("Missing map cell definition.");
                    var turfId = cell.Turf?.Type ?? (World.Variables.GetValueOrDefault("turf").Value is DmTypeReference defaultTurf
                        ? defaultTurf.Id : throw new CompatibilityException("world.turf must contain a type reference for default map filling."));
                    RequireMapType(turfId, "/turf", "turf");
                    var turf = CreateMapTurf(turfId, cell.Turf?.VarOverrides);
                    if (cell.Area?.VarOverrides is not null)
                        throw new CompatibilityException("Map area variable overrides are not implemented.");
                    var areaId = cell.Area?.Type ?? (World.Variables.GetValueOrDefault("area").Value is DmTypeReference defaultArea
                        ? defaultArea.Id : throw new CompatibilityException("world.area must contain a type reference for default map filling."));
                    RequireMapType(areaId, "/area", "area");
                    if (!areas.TryGetValue(areaId, out var area)) {
                        area = CreateMapArea(areaId);
                        areas.Add(areaId, area);
                    }
                    if (cells.ContainsKey((x, y, block.Z))) throw new CompatibilityException("Overlapping map blocks are not implemented.");
                    cells[(x, y, block.Z)] = new(x, y, block.Z, turf.Variables.GetValueOrDefault("density").IsTruthy, turf.TypePath, turf.Variables.GetValueOrDefault("color").Value as string);
                    _mapLocations.Add(turf, new(x, y, block.Z, area));
                    _mapTurfs.Add((x, y, block.Z), turf);
                    var entities = new List<DmObject>();
                    foreach (var entity in cell.Objects) {
                        RequireMapType(entity.Type, "/atom/movable", "entity");
                        if (entity.VarOverrides is { Count: > 0 })
                            throw new CompatibilityException("Map entity variable overrides are not implemented.");
                        var instance = CreateInstance(entity.Type);
                        _mapLocations.Add(instance, new(x, y, block.Z, turf));
                        entities.Add(instance);
                        blockMovables.Add((x, y, instance));
                        _entities.Add(new(x, y, block.Z, instance.TypePath));
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
            if (_mapLocations.TryGetValue(cell.Area, out var first) && first.Z != cell.Z)
                _multiLevelAreas.Add(cell.Area);
            _mapLocations.TryAdd(cell.Area, new(cell.X, cell.Y, cell.Z, null));
        }
        if (_defaultArea is not null) _mapLocations.TryAdd(_defaultArea, new(0, 0, 0, null));
    }

    private DmObject CreateMapTurf(int typeId, Dictionary<string, JsonElement>? overrides) {
        // Definition defaults first, then per-cell raw overrides. Executable turf
        // initializers remain rejected by CreateInstance; no late writes emulate them.
        var turf = CreateInstance(typeId);
        foreach (var (name, value) in overrides ?? []) {
            if (!turf.Variables.ContainsKey(name))
                throw new CompatibilityException($"Map turf override '{name}' names an unknown variable.");
            if ((ProtectedMapField(turf, name) && name is not ("density" or "color" or "name" or "layer")) ||
                name is "type" or "parent_type" or "vars" or "tag")
                throw new CompatibilityException($"Map turf override '{name}' is not implemented.");
            turf.Variables[name] = ConvertValue(value);
        }
        return turf;
    }

    private static DmValue TurfLayer(DmObject turf) {
        // Pinned native storage preserves every float (including infinities), and
        // nonnumbers coerce to zero. No rendering operation or integer cast occurs.
        return turf.Variables.GetValueOrDefault("layer").Value is float number ? number : 0f;
    }

    private bool IsSubtypeOf(int typeId, string ancestorPath) {
        if (typeId < 0 || typeId >= Data.Types.Length) return false;
        for (int? current = typeId; current is not null; current = Data.Types[current.Value].Parent)
            if (Data.Types[current.Value].Path == ancestorPath) return true;
        return false;
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
        ValidateAreaInitializer(area, initializer, new HashSet<Procedure>());
        Execute(new Fiber(new Frame(initializer, area, [])));
        return area;
    }

    private void ValidateAreaInitializer(DmObject area, Procedure proc, HashSet<Procedure> visited) {
        if (!visited.Add(proc)) throw new CompatibilityException($"Area initializer repeats a super initializer at {Project.Location(proc, 0)}.");
        var frame = new Frame(proc, area, []);
        var reader = frame.Reader;
        if (visited.Count > 256) throw Fault(frame, 0, "Area initializer inheritance limit exceeded.");
        if (Data.Types[proc.OwningTypeId].InitProc is not int ownInit || !ReferenceEquals(Data.Procs[ownInit], proc))
            throw Fault(frame, 0, "Area initializer must be a compiler type initializer.");
        void Assignment(Reference reference, int offset) {
            if (reference.Kind != 13) throw Fault(frame, offset, "Area initializer may assign only its own user variables.");
            var name = Project.String(reference.Value);
            if (!area.Variables.ContainsKey(name) || ProtectedMapField(area, name) || name is "type" or "parent_type" or "vars" or "tag")
                throw Fault(frame, offset, $"Area initializer native/metadata assignment '{name}' is not implemented.");
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
                    if (id < 0 || id >= Data.Types.Length) throw Fault(frame, offset, "Area initializer has an invalid type literal.");
                    break;
                case DreamProcOpcode.Assign: case DreamProcOpcode.AssignNoPush: Assignment(reader.Reference(), offset); break;
                case DreamProcOpcode.ReturnReferenceValue:
                    if (reader.Reference().Kind != 2) throw Fault(frame, offset, "Area initializer reference reads are not implemented.");
                    break;
                case DreamProcOpcode.CreateList:
                    if (reader.Int() < 0) throw Fault(frame, offset, "Area initializer has an invalid list length.");
                    break;
                case DreamProcOpcode.PushNFloats: case DreamProcOpcode.PushNStrings:
                case DreamProcOpcode.CreateListNFloats: case DreamProcOpcode.CreateListNStrings: case DreamProcOpcode.CreateListNResources:
                    var count = reader.Int();
                    if (count < 0 || count > (reader.Length - reader.Position) / 4)
                        throw Fault(frame, offset, "Area initializer has an invalid literal list length.");
                    for (var i = 0; i < count; i++) {
                        if (opcode is DreamProcOpcode.CreateListNFloats or DreamProcOpcode.PushNFloats) reader.Float();
                        else Project.String(reader.Int());
                    }
                    break;
                case DreamProcOpcode.PushStringFloat:
                    Project.String(reader.Int()); reader.Float(); break;
                case DreamProcOpcode.Call:
                    var reference = reader.Reference();
                    var kind = reader.Byte(); var argc = reader.Int();
                    if (reference.Kind != 6 || kind is not (0 or 4) || argc != 0)
                        throw Fault(frame, offset, "Area initializer calls other than argument-free super initialization are not implemented.");
                    if (SuperProc(proc) is { } parent) ValidateAreaInitializer(area, parent, visited);
                    break;
                default:
                    throw Fault(frame, offset, $"Area initializer opcode {opcode} is not implemented; only local literal/list defaults are supported.");
            }
        }
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
        while (fiber.Frames.Count > 0) {
            var frame = fiber.Frames[^1];
            var offset = frame.Reader.Position;
            if (--_budget < 0) throw Fault(frame, offset, "Instruction budget exhausted; possible non-yielding loop.");
            try {
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
                        var reference = frame.Reader.Reference(); var args = Arguments(frame);
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
                            if (Suspend(fiber, Math.Max(0, delay))) return;
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
                            _immediateSpawnDepth++;
                            try { Execute(spawned); }
                            finally { _immediateSpawnDepth--; }
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
                    case DreamProcOpcode.Subtract: Binary(frame, (a, b) => a - b); break;
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
                                result.Append(values[index++].ToString());
                            } else throw new CompatibilityException($"String format {suffix} is not implemented.");
                        }
                        if (index != values.Length) throw new CompatibilityException("Unused interpolation values.");
                        frame.Push(result.ToString()); break;
                    }
                    case DreamProcOpcode.CreateList: { var list = new DmList(); list.Values.AddRange(frame.PopMany(frame.Reader.Int())); frame.Push(new(list)); break; }
                    case DreamProcOpcode.CreateListNFloats: case DreamProcOpcode.CreateListNStrings:
                    case DreamProcOpcode.CreateListNResources: case DreamProcOpcode.CreateListNRefs: {
                        var count = frame.Reader.Int();
                        var list = new DmList();
                        for (var i = 0; i < count; i++) list.Values.Add(opcode switch {
                            DreamProcOpcode.CreateListNFloats => new(frame.Reader.Float()),
                            DreamProcOpcode.CreateListNStrings => new(Project.String(frame.Reader.Int())),
                            DreamProcOpcode.CreateListNResources => new(new DmResource(Project.String(frame.Reader.Int()))),
                            _ => Get(frame, frame.Reader.Reference())
                        });
                        frame.Push(new(list)); break;
                    }
                    case DreamProcOpcode.DereferenceIndex: { var index = frame.Pop(); var list = frame.Pop().Value as DmList ?? throw new CompatibilityException("Only list indexing is implemented."); frame.Push(list.Get(index)); break; }
                    case DreamProcOpcode.DereferenceField: { var name = Project.String(frame.Reader.Int()); frame.Push(Field(frame.Pop(), name)); break; }
                    case DreamProcOpcode.PushRefAndDereferenceField: { var value = Get(frame, frame.Reader.Reference()); frame.Push(Field(value, Project.String(frame.Reader.Int()))); break; }
                    case DreamProcOpcode.IsNull: frame.Push(frame.Pop().IsNull ? 1 : 0); break;
                    case DreamProcOpcode.Length: { var value = frame.Pop().Value; frame.Push(value switch { string text => text.Length, DmList list => list.Values.Count, null => 0, _ => throw new CompatibilityException("length() on this value is not implemented.") }); break; }
                    case DreamProcOpcode.PushNRefs: case DreamProcOpcode.PushNFloats: case DreamProcOpcode.PushNStrings: {
                        var count = frame.Reader.Int(); for (var i = 0; i < count; i++) frame.Push(opcode switch {
                            DreamProcOpcode.PushNRefs => Get(frame, frame.Reader.Reference()),
                            DreamProcOpcode.PushNFloats => new(frame.Reader.Float()), _ => new(Project.String(frame.Reader.Int())) }); break;
                    }
                    case DreamProcOpcode.PushStringFloat: frame.Push(Project.String(frame.Reader.Int())); frame.Push(frame.Reader.Float()); break;
                    default: throw new CompatibilityException($"Opcode {opcode} (0x{(byte)opcode:X2}) is not implemented.");
                }
            } catch (CompatibilityException ex) { throw Fault(frame, offset, ex.Message, ex); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException or KeyNotFoundException) { throw Fault(frame, offset, $"Malformed or unsupported execution: {ex.Message}", ex); }
        }
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
        _logs.Add(new(Time, value.ToString()));
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
    private DmValue Field(DmValue owner, string name) {
        if (owner.Value is DmList list && name == "len") return list.Values.Count;
        if (owner.Value is not DmObject obj) throw new CompatibilityException($"Cannot read field '{name}' on this value.");
        if (ReferenceEquals(obj, World)) {
            if (BuiltinWorldFields.Contains(name) && !ReadableWorldFields.Contains(name))
                throw new CompatibilityException($"world.{name} is not implemented.");
            if (name == "time") return (float)Time;
        }
        if (_mapLocations.TryGetValue(obj, out var location)) {
            // The pinned appearance setter retains strings and normalizes every
            // nonstring name to empty. Keep raw definition values untouched.
            if (IsSubtypeOf(obj.TypeId, "/turf")) {
                if (name == "name") return obj.Variables.GetValueOrDefault("name").Value as string ?? "";
                if (name == "layer") return TurfLayer(obj);
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
        return obj.Variables.TryGetValue(name, out var value) ? value : throw new CompatibilityException($"Unknown field '{name}'.");
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
    private static DmValue Index(Frame frame) { var index = frame.Pop(); var list = frame.Pop().Value as DmList ?? throw new CompatibilityException("Only list indexing is implemented."); return list.Get(index); }
    private void Set(Frame frame, Reference reference, DmValue value) {
        switch (reference.Kind) {
            case 2: frame.Result = value; break;
            case 8: if (reference.Value >= frame.Arguments.Length) Array.Resize(ref frame.Arguments, reference.Value + 1); frame.Arguments[reference.Value] = value; break;
            case 9: frame.Locals[reference.Value] = value; break;
            case 10: _globals[reference.Value] = value; break;
            case 12: SetField(frame.Pop(), Project.String(reference.Value), value); break;
            case 13: SetField(new(frame.Src), Project.String(reference.Value), value); break;
            case 7: { var index = frame.Pop(); var list = frame.Pop().Value as DmList ?? throw new CompatibilityException("Only list indexing is implemented."); list.Set(index, value); break; }
            default: throw new CompatibilityException($"Write reference kind {reference.Kind} is not implemented.");
        }
    }
    private void SetField(DmValue owner, string name, DmValue value) {
        if (owner.Value is not DmObject obj || !obj.Variables.ContainsKey(name)) throw new CompatibilityException($"Cannot assign field '{name}'.");
        if (_mapLocations.ContainsKey(obj) && ProtectedMapField(obj, name))
            throw new CompatibilityException($"Mutation of map atom.{name} is not implemented.");
        if (ReferenceEquals(obj, World) && BuiltinWorldFields.Contains(name)) throw new CompatibilityException($"Mutation of world.{name} is not implemented.");
        if (name is "type" or "parent_type" or "vars")
            throw new CompatibilityException($"Mutation of datum.{name} is not implemented.");
        obj.Variables[name] = value;
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
        public Frame(Procedure proc, DmObject? src, DmValue[] args) {
            Proc = proc; Reader = new(proc.Bytecode ?? []); Src = src; Arguments = [.. args]; Locals = new DmValue[Math.Max(256, proc.MaxVariableId + 1)];
        }
        public void Push(DmValue value) => Stack.Add(value);
        public DmValue Pop() { if (Stack.Count == 0) throw new CompatibilityException("DM evaluation stack underflow."); var result = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); return result; }
        public DmValue[] PopMany(int count) { if (count < 0 || count > Stack.Count) throw new CompatibilityException("Invalid DM stack argument count."); var result = Stack.GetRange(Stack.Count - count, count).ToArray(); Stack.RemoveRange(Stack.Count - count, count); return result; }
        public Frame Clone() { var frame = new Frame(Proc, Src, Arguments) { Usr = Usr, Result = Result, Locals = [.. Locals] }; frame.Reader.Position = Reader.Position; frame.Stack.AddRange(Stack); return frame; }
    }
}
