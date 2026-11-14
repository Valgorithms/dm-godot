using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DmGodot.Runtime;

// This host deliberately exposes a transport test grid, not DM mob lifecycle.
return await Host.Run(args);

internal static class Host {
    public static async Task<int> Run(string[] args) {
        if (args.Contains("--help") || args.Length == 0) {
            Console.WriteLine("Usage: DmGodot.Host --project <compiled.json> --diagnostic-grid [--port 25566] [--duration 0]");
            Console.WriteLine("Loopback-only diagnostic host. Player markers are not DM mobs or movement callbacks.");
            return args.Length == 0 ? 2 : 0;
        }
        try {
            string? projectPath = null;
            var port = 25566;
            var duration = 0.0;
            var diagnostic = false;
            for (var i = 0; i < args.Length; i++) {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value after {args[i - 1]}.");
                switch (args[i]) {
                    case "--project": projectPath = Next(); break;
                    case "--port": port = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--duration": duration = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--diagnostic-grid": diagnostic = true; break;
                    default: throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            if (projectPath is null || !diagnostic) throw new ArgumentException("--project and --diagnostic-grid are required.");
            if (port is < 0 or > 65535 || !double.IsFinite(duration) || duration < 0) throw new ArgumentException("Invalid port or duration.");
            var runtime = new DmRuntime(CompiledProject.Load(projectPath));
            runtime.Boot();
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; stop.Cancel(); };
            if (duration > 0) stop.CancelAfter(TimeSpan.FromSeconds(duration));
            await new DiagnosticServer(runtime).Run(port, stop.Token);
            return 0;
        } catch (Exception ex) when (ex is CompatibilityException or ArgumentException or IOException or SocketException or FormatException or OverflowException) {
            Console.Error.WriteLine($"HOST_ERROR: {ex.Message}");
            return 1;
        }
    }
}

internal sealed class DiagnosticServer(DmRuntime runtime) {
    private readonly Channel<Inbound> _input = Channel.CreateBounded<Inbound>(256);
    private readonly Dictionary<string, Peer> _peers = [];
    private readonly Dictionary<string, Player> _players = [];
    private long _revision;
    private int _logCursor;
    private bool _dirty;

    public async Task Run(int port, CancellationToken token) {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(16);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var accept = Accept(listener, linked.Token);
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        Console.WriteLine(JsonSerializer.Serialize(new { type = "listening", port = ((IPEndPoint)listener.LocalEndpoint).Port, protocol = 1, mode = "diagnostic-grid" }));
        try {
            while (!token.IsCancellationRequested) {
                for (var count = 0; count < 256 && _input.Reader.TryRead(out var item); count++) Process(item);
                var now = clock.Elapsed.TotalSeconds;
                runtime.AdvanceBy((now - last) * 10.0);
                last = now;
                while (_logCursor < runtime.Logs.Count) {
                    var log = runtime.Logs[_logCursor++];
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "dm-log", time = log.Time, text = log.Text }));
                    Broadcast(new { type = "log", text = log.Text });
                }
                if (_dirty) { _revision++; Broadcast(Snapshot()); _dirty = false; }
                await Task.Delay(10, token);
                if (accept.IsFaulted) await accept;
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
        } finally {
            linked.Cancel();
            listener.Stop();
            foreach (var peer in _peers.Values) peer.Close();
            try { await accept; } catch (OperationCanceledException) { } catch (SocketException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task Accept(TcpListener listener, CancellationToken token) {
        var next = 0;
        while (!token.IsCancellationRequested) {
            var socket = await listener.AcceptTcpClientAsync(token);
            socket.NoDelay = true;
            var peer = new Peer((++next).ToString(System.Globalization.CultureInfo.InvariantCulture), socket, _input.Writer, token);
            await _input.Writer.WriteAsync(new(peer, null, "connected"), token);
            peer.Start();
        }
    }

    private void Process(Inbound item) {
        var peer = item.Peer;
        if (item.Event == "connected") {
            if (_peers.Count >= 16) { peer.Close(); return; }
            _peers.Add(peer.Id, peer);
            return;
        }
        if (!_peers.ContainsKey(peer.Id)) return;
        if (item.Event == "disconnected") { _peers.Remove(peer.Id); _dirty |= _players.Remove(peer.Id); peer.Close(); return; }
        if (item.Event is not null) { peer.Send(new { type = "error", text = item.Event }); return; }
        var message = item.Message!.Value;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) {
            peer.Send(new { type = "error", text = "Expected an object with a string type." }); return;
        }
        switch (type.GetString()) {
            case "hello":
                if (_players.ContainsKey(peer.Id)) { peer.Send(new { type = "error", text = "Already joined." }); return; }
                if (!message.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 40) {
                    peer.Send(new { type = "error", text = "name must contain 1 to 40 characters." }); return;
                }
                var tile = runtime.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X)
                    .FirstOrDefault(t => t.Z == 1 && !t.Density && !_players.Values.Any(p => p.X == t.X && p.Y == t.Y));
                if (tile is null) { peer.Send(new { type = "error", text = "No free diagnostic-grid spawn tile." }); return; }
                _players.Add(peer.Id, new(peer.Id, name.GetString()!, tile.X, tile.Y));
                peer.Send(new { type = "welcome", id = peer.Id, protocol = 1 });
                peer.Send(new { type = "log", text = "Diagnostic player markers; DM mob lifecycle and movement callbacks are not implemented." });
                _dirty = true;
                break;
            case "move":
                if (!_players.TryGetValue(peer.Id, out var player)) { peer.Send(new { type = "error", text = "Send hello before moving." }); return; }
                if (!message.TryGetProperty("dx", out var x) || !message.TryGetProperty("dy", out var y)
                    || x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number
                    || !x.TryGetInt32(out var dx) || !y.TryGetInt32(out var dy)
                    || dx is < -1 or > 1 || dy is < -1 or > 1 || Math.Abs(dx) + Math.Abs(dy) != 1) {
                    peer.Send(new { type = "error", text = "Movement must be one cardinal integer step." }); return;
                }
                var tx = player.X + dx;
                var ty = player.Y + dy;
                if (runtime.Tiles.Any(t => t.Z == 1 && t.X == tx && t.Y == ty && !t.Density)
                    && !_players.Values.Any(p => p.X == tx && p.Y == ty)) {
                    _players[peer.Id] = player with { X = tx, Y = ty };
                    _dirty = true;
                } else peer.Send(Snapshot());
                break;
            default: peer.Send(new { type = "error", text = "Unsupported protocol command." }); break;
        }
    }

    private object Snapshot() => new {
        type = "snapshot", width = runtime.Width, height = runtime.Height, revision = _revision,
        tiles = runtime.Tiles.Where(t => t.Z == 1).Select(t => new { x = t.X, y = t.Y, z = t.Z, density = t.Density, type = t.Type }),
        players = _players.Values.Select(p => new { id = p.Id, name = p.Name, x = p.X, y = p.Y, z = 1 })
    };
    private void Broadcast(object value) { foreach (var id in _players.Keys) _peers[id].Send(value); }
    private sealed record Player(string Id, string Name, int X, int Y);
}

internal sealed record Inbound(Peer Peer, JsonElement? Message, string? Event = null);

internal sealed class Peer(string id, TcpClient socket, ChannelWriter<Inbound> input, CancellationToken serverToken) {
    public string Id { get; } = id;
    private readonly Channel<string> _output = Channel.CreateBounded<string>(128);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
    public void Start() { _ = Read(); _ = Write(); }
    public void Send(object message) { if (!_output.Writer.TryWrite(JsonSerializer.Serialize(message))) Close(); }
    public void Close() { _stop.Cancel(); _output.Writer.TryComplete(); socket.Dispose(); }

    private async Task Read() {
        try {
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false, true));
            var buffer = new char[1024];
            var pending = new StringBuilder();
            while (true) {
                var count = await reader.ReadAsync(buffer, _stop.Token);
                if (count == 0) break;
                for (var i = 0; i < count; i++) {
                    if (buffer[i] == '\n') {
                        try {
                            using var doc = JsonDocument.Parse(pending.ToString());
                            await input.WriteAsync(new(this, doc.RootElement.Clone()), _stop.Token);
                        } catch (JsonException) { await input.WriteAsync(new(this, null, "Malformed JSON."), _stop.Token); }
                        pending.Clear();
                    } else if (pending.Length < 16384) pending.Append(buffer[i]);
                    else throw new IOException("Protocol line exceeds 16 KiB.");
                }
            }
        } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or DecoderFallbackException) {
        } finally {
            Close();
            try { await input.WriteAsync(new(this, null, "disconnected"), serverToken); } catch (OperationCanceledException) { }
        }
    }

    private async Task Write() {
        try {
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var line in _output.Reader.ReadAllAsync(_stop.Token)) await writer.WriteLineAsync(line.AsMemory(), _stop.Token);
        } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) {
        } finally { Close(); }
    }
}
