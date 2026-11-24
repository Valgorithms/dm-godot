namespace DmGodot.Runtime;

/// <summary>Owns a fresh runtime until global initialization, map callbacks, world.New
/// and the initial deferred work have completed. Dispose an abandoned session.</summary>
public sealed class DmStartupSession : IDisposable {
    private readonly object _gate = new();
    private DmRuntime? _candidate;
    private CancellationToken _cancellation;
    private CancellationTokenRegistration _registration;
    private bool _initializedGlobals;

    // Scalar diagnostics from the last completed advance, including a failed advance.
    public int LogCount { get; private set; }
    public int PendingThreads { get; private set; }

    public DmStartupSession(CompiledProject project, DmFileInventory? fileInventory = null,
        DmTextFileSnapshot? textFiles = null, CancellationToken cancellationToken = default) {
        _candidate = new DmRuntime(project, fileInventory, textFiles, cancellationToken);
        _cancellation = cancellationToken;
        if (cancellationToken.CanBeCanceled) {
            // A long-lived token must not keep an abandoned world alive. Cancellation
            // drops a parked world immediately, without waiting for an active phase.
            var owner = new WeakReference<DmStartupSession>(this);
            _registration = cancellationToken.UnsafeRegister(static state => {
                var owner = (WeakReference<DmStartupSession>)state!;
                if (owner.TryGetTarget(out var session))
                    Interlocked.Exchange(ref session._candidate, null);
            }, owner);
        }
    }

    /// <summary>The first call runs the global initializer. The second transfers the
    /// completed runtime. A fault or cancellation closes the session.</summary>
    public DmRuntime? Advance() {
        lock (_gate) {
            var runtime = Interlocked.Exchange(ref _candidate, null);
            try {
                _cancellation.ThrowIfCancellationRequested();
                if (runtime is null) throw new InvalidOperationException("The startup session has closed.");
                if (!_initializedGlobals) {
                    runtime.InitializeGlobals();
                    _initializedGlobals = true;
                    Interlocked.Exchange(ref _candidate, runtime);
                    // If cancellation ran while the runtime was local, undo this park.
                    _cancellation.ThrowIfCancellationRequested();
                    return null;
                }
                runtime.FinishBoot();
                _cancellation.ThrowIfCancellationRequested();
                Close();
                return runtime;
            } catch {
                Close();
                throw;
            } finally {
                if (runtime is not null) {
                    LogCount = runtime.Logs.Count;
                    PendingThreads = runtime.PendingThreads;
                }
            }
        }
    }

    private void Close() {
        Interlocked.Exchange(ref _candidate, null);
        _registration.Dispose();
        _registration = default;
        _cancellation = default;
    }

    public void Dispose() {
        lock (_gate) Close();
    }
}
