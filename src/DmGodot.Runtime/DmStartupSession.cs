namespace DmGodot.Runtime;

/// <summary>Finite cumulative work allowances for the global initializer of an owned startup session.</summary>
public enum DmGlobalInitializationProfile {
    Standard = DmRuntime.DefaultInstructionBudget,
    Extended = 2 * DmRuntime.DefaultInstructionBudget
}

/// <summary>Admitted work units in one global-initializer step and the remaining cumulative allowance.</summary>
public readonly record struct DmGlobalWork(int StepSpent, int Remaining);

/// <summary>Owns a fresh runtime until global initialization, map callbacks, world.New
/// and the initial deferred work have completed. Dispose an abandoned session.</summary>
public sealed class DmStartupSession : IDisposable {
    private readonly object _gate = new();
    private readonly DmGlobalInitializationProfile _globalProfile;
    private DmRuntime? _candidate;
    private CancellationToken _cancellation;
    private CancellationTokenRegistration _registration;
    private bool _initializedGlobals;
    private DmGlobalWork? _globalWork;

    /// <summary>The most recent global-initializer step, including a faulted step. Null before the first step.</summary>
    public DmGlobalWork? GlobalWork {
        get { lock (_gate) return _globalWork; }
    }

    // Scalar diagnostics from the last completed advance, including a failed advance.
    public int LogCount { get; private set; }
    public int PendingThreads { get; private set; }

    public DmStartupSession(CompiledProject project, DmFileInventory? fileInventory,
        DmTextFileSnapshot? textFiles, CancellationToken cancellationToken)
        : this(project, fileInventory, textFiles, cancellationToken, null) { }

    public DmStartupSession(CompiledProject project, DmFileInventory? fileInventory = null,
        DmTextFileSnapshot? textFiles = null, CancellationToken cancellationToken = default, DmMapIconInventory? mapIcons = null)
        : this(project, fileInventory, textFiles, cancellationToken, mapIcons, DmGlobalInitializationProfile.Standard) { }

    public DmStartupSession(CompiledProject project, DmFileInventory? fileInventory, DmTextFileSnapshot? textFiles,
        CancellationToken cancellationToken, DmMapIconInventory? mapIcons, DmGlobalInitializationProfile globalProfile) {
        if (globalProfile is not (DmGlobalInitializationProfile.Standard or DmGlobalInitializationProfile.Extended))
            throw new ArgumentOutOfRangeException(nameof(globalProfile));
        _globalProfile = globalProfile;
        _candidate = new DmRuntime(project, fileInventory, textFiles, cancellationToken, mapIcons);
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

    /// <summary>Run up to maxDispatches charged interpreter dispatches during global initialization.
    /// True means globals are complete. Native operations run atomically under the
    /// cumulative work budget. This method never runs the remaining boot phases.</summary>
    public bool AdvanceGlobals(int maxDispatches) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDispatches);
        lock (_gate) {
            AdvanceCore(maxDispatches, globalsOnly: true);
            return _initializedGlobals;
        }
    }

    /// <summary>Complete any remaining global initialization, or transfer the
    /// fully booted runtime if globals were already complete on entry.</summary>
    public DmRuntime? Advance() {
        lock (_gate) return AdvanceCore(long.MaxValue, globalsOnly: false);
    }

    private DmRuntime? AdvanceCore(long maxDispatches, bool globalsOnly) {
        var runtime = Interlocked.Exchange(ref _candidate, null);
        try {
            _cancellation.ThrowIfCancellationRequested();
            if (runtime is null) throw new InvalidOperationException("The startup session has closed.");
            if (globalsOnly || !_initializedGlobals) {
                if (!_initializedGlobals) _initializedGlobals = runtime.AdvanceGlobalInitialization(maxDispatches, _globalProfile);
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
                _globalWork = runtime.LastGlobalWork;
                LogCount = runtime.Logs.Count;
                PendingThreads = runtime.PendingThreads;
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
