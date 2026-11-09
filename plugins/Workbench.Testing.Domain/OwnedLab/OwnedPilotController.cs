namespace Workbench.Testing.Domain.OwnedLab;

public sealed class OwnedPilotController
{
    private readonly object _sync = new();
    private readonly OwnedScope _scope;
    private readonly IOwnedDriverFactory _factory;
    private readonly TimeProvider _time;
    private readonly OwnedAtomicFiles _files;
    private readonly HashSet<Task> _reads = [];
    private OwnedJournal? _journal;
    private Task? _initialization, _active, _stop, _leave, _notification;
    private OwnedRevocation? _authority;
    private OwnedRunSnapshot? _selected;
    private int _seals;
    private bool _stopped, _busy, _notificationsClosed;
    private string? _error;
    private (OwnedPilotSnapshot Snapshot, Action<OwnedPilotSnapshot> Handlers)? _pending;

    public OwnedPilotController(OwnedScope scope, IOwnedDriverFactory factory, TimeProvider time) : this(scope, factory, time, new OwnedAtomicFiles()) { }
    internal OwnedPilotController(OwnedScope scope, IOwnedDriverFactory factory, TimeProvider time, OwnedAtomicFiles files)
    { _scope = scope with { DataDirectory = Path.GetFullPath(scope.DataDirectory) }; _factory = factory; _time = time; _files = files; }
    public event Action<OwnedPilotSnapshot>? Changed;
    public OwnedPilotSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var runs = _journal?.Runs ?? [];
                var summaries = Array.AsReadOnly(runs.Select(r => new OwnedRunSummary(r.Execution, r.Input.Scenario, r.State, r.Sequence, r.HeadSha)).ToArray());
                var selected = _selected is null ? null : runs.FirstOrDefault(r => r.Execution.RunId == _selected.Execution.RunId) ?? _selected;
                return new(summaries, selected is null ? null : OwnedJournal.Copy(selected), _busy, _stopped || _seals != 0, _error);
            }
        }
    }

    public IDisposable SealAdmission()
    {
        OwnedRevocation? authority;
        lock (_sync) { _seals++; authority = _authority; authority?.Gate.Revoke(); }
        authority?.Request("Cancelled"); Publish(); return new AdmissionSeal(this);
    }
    private sealed class AdmissionSeal(OwnedPilotController owner) : IDisposable
    {
        private OwnedPilotController? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null); if (current is null) return;
            lock (current._sync) current._seals--; current.Publish();
        }
    }
    private void Open()
    {
        if (_stopped || _seals != 0) throw new OwnedLabException("AdmissionClosed");
        if (_journal is null) throw new OwnedLabException("InvalidPlan");
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource start; Task work;
        lock (_sync)
        {
            if (_initialization is not null) return _initialization;
            if (_stopped || _seals != 0) return Task.FromException(new OwnedLabException("AdmissionClosed"));
            start = new(TaskCreationOptions.RunContinuationsAsynchronously); work = InitializeCoreAsync(start.Task, cancellationToken); _initialization = work;
        }
        start.SetResult(); return work;
    }
    private async Task InitializeCoreAsync(Task admission, CancellationToken cancellationToken)
    {
        await admission.ConfigureAwait(false);
        try
        {
            var journal = await OwnedJournal.OpenAsync(_scope, _files, cancellationToken).ConfigureAwait(false);
            lock (_sync) { _journal = journal; if (journal.IsReadOnly) _error = "RecoveryNeeded"; }
            Publish(); await DrainNotificationsAsync().ConfigureAwait(false);
        }
        catch (Exception error) { SetError(error is OwnedLabException e ? e.ErrorCode : error is OperationCanceledException ? "Cancelled" : "StoreIoFailure"); throw; }
    }

    public Task<OwnedRunSnapshot> RunAsync(OwnedScenario scenario, CancellationToken cancellationToken = default) => Start(() => (FixedOwnedPlans.Create(scenario), null), cancellationToken);
    public Task<OwnedRunSnapshot> RetryAsync(string runId, CancellationToken cancellationToken = default) => Start(() =>
    {
        var run = _journal!.Read(runId);
        if (_journal.IsReadOnly || run.RecoveryNeeded) throw new OwnedLabException("RecoveryNeeded");
        if (run.State is not (OwnedRunState.Passed or OwnedRunState.Failed or OwnedRunState.Cancelled or OwnedRunState.Interrupted)) throw new OwnedLabException("AdmissionClosed");
        return (FixedOwnedPlans.ParseFrozen(run.Input.Bytes), run);
    }, cancellationToken);

    private Task<OwnedRunSnapshot> Start(Func<(OwnedInputPlan Input, OwnedRunSnapshot? Parent)> capture, CancellationToken cancellationToken)
    {
        TaskCompletionSource start; Task<OwnedRunSnapshot> work;
        lock (_sync)
        {
            try
            {
                Open(); cancellationToken.ThrowIfCancellationRequested();
                if (_journal!.IsReadOnly) throw new OwnedLabException("RecoveryNeeded");
                // The final idle callback is informational; admission remains owned until its actual task returns.
                if (_busy || _active is { IsCompleted: false }) throw new OwnedLabException("AdmissionClosed");
                var frozen = capture(); _authority = new(); _busy = true; _selected = null; _error = null;
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                work = RunCoreAsync(start.Task, frozen.Input, frozen.Parent, _authority, cancellationToken); _active = work;
            }
            catch (Exception error) { return Task.FromException<OwnedRunSnapshot>(error); }
        }
        Publish(); start.SetResult(); return work;
    }
    private async Task<OwnedRunSnapshot> RunCoreAsync(Task admission, OwnedInputPlan input, OwnedRunSnapshot? parent, OwnedRevocation authority, CancellationToken cancellationToken)
    {
        await admission.ConfigureAwait(false); authority.Observe(cancellationToken);
        try { return await OwnedRunner.RunAsync(_journal!, input, parent, _factory, _time, authority, Progress).ConfigureAwait(false); }
        catch (Exception error) { SetError(error is OwnedLabException e ? e.ErrorCode : "DriverFailure"); throw; }
        finally
        {
            await DrainNotificationsAsync().ConfigureAwait(false);
            lock (_sync) { _busy = false; _authority = null; }
            Publish(); await DrainNotificationsAsync().ConfigureAwait(false);
        }
    }
    private void Progress(OwnedRunSnapshot run)
    {
        lock (_sync)
        {
            if (_selected is null || _selected.Execution.RunId == run.Execution.RunId) _selected = OwnedJournal.Copy(run);
            _error = run.ErrorCode;
        }
        Publish();
    }

    public Task<OwnedRunSnapshot> SelectAsync(string runId, CancellationToken cancellationToken = default) => ReadOperation(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested(); var run = _journal!.Read(runId);
        lock (_sync) _selected = run; Publish(); await DrainNotificationsAsync().ConfigureAwait(false); return run;
    });
    public Task<byte[]> ReadFrameAsync(string runId, int sequence, CancellationToken cancellationToken = default) => ReadOperation(async () =>
    { cancellationToken.ThrowIfCancellationRequested(); return await _journal!.ReadFrameAsync(runId, sequence).ConfigureAwait(false); });

    private Task<T> ReadOperation<T>(Func<Task<T>> operation)
    {
        TaskCompletionSource start; Task<T> work;
        lock (_sync)
        {
            try { Open(); } catch (Exception error) { return Task.FromException<T>(error); }
            start = new(TaskCreationOptions.RunContinuationsAsynchronously); work = ReadCoreAsync(start.Task, operation); _reads.Add(work);
        }
        _ = work.ContinueWith(done => { _ = done.Exception; lock (_sync) _reads.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        start.SetResult(); return work;
    }
    private static async Task<T> ReadCoreAsync<T>(Task admission, Func<Task<T>> operation) { await admission.ConfigureAwait(false); return await operation().ConfigureAwait(false); }

    public Task CancelAndDrainAsync() => DrainWithSealAsync(SealAdmission(), default);
    public Task PrepareLeaveAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_leave is { IsCompleted: false }) return _leave;
            if (_stopped) return Task.FromException(new OwnedLabException("AdmissionClosed"));
            return _leave = DrainWithSealAsync(SealAdmission(), cancellationToken);
        }
    }
    private async Task DrainWithSealAsync(IDisposable seal, CancellationToken cancellationToken)
    {
        try { await DrainOperationsAsync().ConfigureAwait(false); await DrainNotificationsAsync().ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
        finally { seal.Dispose(); await DrainNotificationsAsync().ConfigureAwait(false); }
    }
    private async Task DrainOperationsAsync()
    {
        Task[] actual; lock (_sync) actual = _reads.Concat(new[] { _initialization, _active }.OfType<Task>()).ToArray();
        await JoinAsync(actual).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource start; Task work; OwnedRevocation? authority;
        lock (_sync)
        {
            if (_stop is not null) return _stop;
            _stopped = true; authority = _authority; authority?.Gate.Revoke();
            start = new(TaskCreationOptions.RunContinuationsAsynchronously); work = StopCoreAsync(start.Task, cancellationToken); _stop = work;
        }
        authority?.Request("Cancelled"); Publish(); start.SetResult(); return work;
    }
    private async Task StopCoreAsync(Task admission, CancellationToken cancellationToken)
    {
        await admission.ConfigureAwait(false); await DrainOperationsAsync().ConfigureAwait(false);
        Task? leave; lock (_sync) leave = _leave; if (leave is not null) await JoinAsync([leave]).ConfigureAwait(false);
        await DrainNotificationsAsync().ConfigureAwait(false);
        if (_journal is not null) await _journal.DisposeAsync().ConfigureAwait(false);
        lock (_sync) _notificationsClosed = true; cancellationToken.ThrowIfCancellationRequested();
    }

    private void SetError(string code) { lock (_sync) _error = code; Publish(); }
    private void Publish()
    {
        TaskCompletionSource? start = null;
        lock (_sync)
        {
            if (_notificationsClosed || Changed is not { } handlers) return;
            _pending = (Snapshot, handlers);
            if (_notification is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously); _notification = NotifyAsync(start.Task);
            }
        }
        start?.SetResult();
    }
    private async Task NotifyAsync(Task admission)
    {
        await admission.ConfigureAwait(false);
        while (true)
        {
            (OwnedPilotSnapshot Snapshot, Action<OwnedPilotSnapshot> Handlers)? next;
            lock (_sync) { next = _pending; _pending = null; if (next is null) { _notification = null; return; } }
            foreach (Action<OwnedPilotSnapshot> handler in next.Value.Handlers.GetInvocationList())
                try { handler(next.Value.Snapshot); }
                catch (Exception) { lock (_sync) _error ??= "DriverFailure"; }
        }
    }
    internal async Task DrainNotificationsAsync()
    {
        while (true) { Task? task; lock (_sync) task = _notification; if (task is null) return; await task.ConfigureAwait(false); }
    }
    private static async Task JoinAsync(IEnumerable<Task> tasks) { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { /* Every admitted task is joined and its fault observed. */ } }
}
