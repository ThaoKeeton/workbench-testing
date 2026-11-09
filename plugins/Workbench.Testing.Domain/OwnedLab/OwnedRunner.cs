namespace Workbench.Testing.Domain.OwnedLab;

internal static class OwnedRunner
{
    internal static async Task<OwnedRunSnapshot> RunAsync(OwnedJournal journal, OwnedInputPlan input, OwnedRunSnapshot? parent,
        IOwnedDriverFactory factory, TimeProvider clock, OwnedRevocation authority, Action<OwnedRunSnapshot> progress)
    {
        IOwnedPreparation? preparation = null; IOwnedSession? session = null; OwnedGrant? grant = null;
        var known = journal.Runs.Select(r => r.Execution.RunId).ToHashSet(StringComparer.Ordinal);
        string? runId = null, errorCode = null; var launchAttempted = false;
        OwnedRetirement? retirement = null;
        try
        {
            if (parent is not null)
            {
                parent = await journal.VerifyRetryAsync(parent.Execution.RunId).ConfigureAwait(false);
                input = FixedOwnedPlans.ParseFrozen(parent.Input.Bytes);
            }
            authority.Check();
            preparation = await factory.PrepareAsync(authority.Token).ConfigureAwait(false);
            var helper = preparation.Helper with { }; OwnedJournalFormat.ValidateHelper(helper);
            authority.Check();
            grant = await journal.CreateAsync(input, helper, clock, parent).ConfigureAwait(false); runId = grant.Execution.RunId;
            authority.Bind(clock, grant.DeadlineTimestamp); grant = grant with { LaunchGate = authority.Gate };
            progress(await journal.AppendAsync(runId, new(OwnedEventKind.Queued)).ConfigureAwait(false));
            authority.Check(); launchAttempted = true;
            session = await preparation.LaunchAsync(grant, authority.Token).ConfigureAwait(false);
            authority.Check();
            var identity = session.Identity with { }; var initial = session.InitialState with { };
            OwnedWireCodec.ValidateIdentity(identity); OwnedWireCodec.ValidateState(initial);
            if (identity.ImageSha != grant.Execution.Helper.ImageSha || initial != new OwnedState("", "", "", "0")) throw new OwnedLabException("ProtocolViolation");
            progress(await journal.AppendAsync(runId, new(OwnedEventKind.Started, Identity: identity, State: initial)).ConfigureAwait(false));
            for (var index = 0; index < input.Steps.Count; index++)
            {
                authority.Check();
                progress(await journal.AppendAsync(runId, new(OwnedEventKind.StepStarted, StepIndex: index)).ConfigureAwait(false));
                authority.Check();
                OwnedObservation observation;
                await using (var commandTimer = authority.CommandDeadline(clock, out var commandDeadline))
                {
                    observation = await session.ExecuteAsync(input.Steps[index].Action, authority.Token).ConfigureAwait(false);
                    // Freeze the returned buffer before even asynchronous timer disposal can yield.
                    observation = observation with { Bmp = observation.Bmp.ToArray() };
                    // Timer delivery may lag its monotonic deadline; revoke without discarding this valid return.
                    if (clock.GetTimestamp() >= commandDeadline) authority.Request("Expired");
                }
                if (observation.Identity != identity) throw new OwnedLabException("ProtocolViolation");
                OwnedWireCodec.ValidateState(observation.State);
                try { OwnedWireCodec.ValidateBmp(observation.Bmp); }
                catch (OwnedLabException) { throw new OwnedLabException("CaptureFailure"); }
                var passed = OwnedJournalFormat.AssertionPasses(input.Steps[index].Action, observation.State);
                progress(await journal.AppendAsync(runId, new(OwnedEventKind.StepResult, index, passed ? OwnedOutcome.Passed : OwnedOutcome.Failed,
                    passed ? null : "AssertionFailed", observation.Identity, observation.State, observation.Bmp)).ConfigureAwait(false));
                if (!passed) { errorCode = "AssertionFailed"; break; }
                authority.Check();
            }
        }
        catch (Exception error)
        {
            errorCode = Code(error, authority);
            if (error is OwnedLaunchException launch && launchAttempted && session is null)
            {
                var receipt = launch.Retirement;
                if (receipt is { ActiveProcesses: 0 } && (receipt.WasLaunched ? receipt.BrokerExitCode is not null : receipt.BrokerExitCode is null && receipt.TargetExitCode is null)) retirement = receipt;
            }
            runId ??= journal.Runs.FirstOrDefault(r => !known.Contains(r.Execution.RunId))?.Execution.RunId;
        }

        if (errorCode is not null) authority.Request(errorCode);
        if (session is not null)
        {
            try
            {
                retirement = await session.RetireAsync(Reason(errorCode)).ConfigureAwait(false);
                if (!ValidSessionRetirement(retirement)) { journal.Freeze(); retirement = null; }
                else
                {
                    errorCode ??= authority.ErrorCode;
                    if (errorCode is null && retirement is not { BrokerExitCode: 0, TargetExitCode: 0 }) errorCode = "DriverFailure";
                }
            }
            catch (OwnedRetirementException failure)
            {
                if (ValidSessionRetirement(failure.Retirement))
                {
                    retirement = failure.Retirement;
                    // An owner cancellation/expiry observed while Close joined precedes its IO error.
                    errorCode ??= authority.ErrorCode ?? failure.ErrorCode;
                    authority.Request(errorCode);
                }
                else { journal.Freeze(); retirement = null; }
            }
            catch (Exception) { journal.Freeze(); }
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { journal.Freeze(); }
        }
        else if (launchAttempted && retirement is null) journal.Freeze(); // Unknown launch failures carry no process-retirement proof.
        else if (!launchAttempted) retirement = new(false, null, null, 0);

        // The caller registration, timers, and every cancellation callback are part of this operation.
        await authority.FinishAsync().ConfigureAwait(false);
        if (preparation is not null)
            try { await preparation.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { journal.Freeze(); errorCode ??= "DriverFailure"; }
        errorCode ??= authority.ErrorCode;
        if (runId is null) throw new OwnedLabException(errorCode ?? "DriverFailure");
        if (journal.IsReadOnly) { var recovery = journal.Read(runId); progress(recovery); return recovery; }
        if (retirement is null) { journal.Freeze(); var recovery = journal.Read(runId); progress(recovery); return recovery; }
        var current = journal.Read(runId);
        var outcome = errorCode is null ? OwnedOutcome.Passed : errorCode is "Cancelled" or "AdmissionClosed" ? OwnedOutcome.Cancelled : OwnedOutcome.Failed;
        try
        {
            var terminal = await journal.AppendAsync(runId, new(OwnedEventKind.Terminal, Outcome: outcome, ErrorCode: errorCode, Retirement: retirement,
                UncertainStep: current.Events.LastOrDefault()?.Kind == OwnedEventKind.StepStarted)).ConfigureAwait(false);
            progress(terminal); return terminal;
        }
        catch (Exception) { journal.Freeze(); var recovery = journal.Read(runId); progress(recovery); return recovery; }
    }

    private static string Code(Exception error, OwnedRevocation authority) => error switch
    { OwnedLabException owned => owned.ErrorCode, OperationCanceledException => authority.ErrorCode ?? "Cancelled", _ => "DriverFailure" };
    private static OwnedRetirementReason Reason(string? code) => code switch
    { null => OwnedRetirementReason.Completed, "Cancelled" => OwnedRetirementReason.Cancelled, "AdmissionClosed" => OwnedRetirementReason.Revoked, "Expired" => OwnedRetirementReason.Expired, _ => OwnedRetirementReason.Failed };
    private static bool ValidSessionRetirement(OwnedRetirement? receipt) => receipt is
        { WasLaunched: true, BrokerExitCode: not null, TargetExitCode: not null, ActiveProcesses: 0 };
}

internal sealed class OwnedRevocation
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _source = new();
    private readonly List<Task> _callbacks = [];
    private CancellationTokenRegistration _caller;
    private ITimer? _lifetime;
    private TimeProvider? _clock;
    private long _deadline;
    private bool _bound, _finished;
    private string? _error;
    internal OwnedLaunchGate Gate { get; } = new();
    internal CancellationToken Token => _source.Token;
    internal string? ErrorCode { get { lock (_sync) return _error; } }
    internal void Observe(CancellationToken caller) => _caller = caller.UnsafeRegister(_ => Request("Cancelled"), null);
    internal void Bind(TimeProvider clock, long deadline)
    {
        _deadline = deadline; _bound = true; _clock = clock; Gate.Bind(clock, deadline);
        var remaining = clock.GetElapsedTime(clock.GetTimestamp(), deadline);
        if (remaining <= TimeSpan.Zero) Request("Expired");
        else _lifetime = clock.CreateTimer(_ => Request("Expired"), null, remaining, Timeout.InfiniteTimeSpan);
    }
    internal void Request(string error)
    {
        // Never schedule callbacks before the synchronous launch authority has been revoked.
        Gate.Revoke();
        lock (_sync)
        {
            if (_finished) return;
            _error ??= error;
            _callbacks.Add(_source.CancelAsync());
        }
    }
    internal void Check()
    {
        if (_bound && _clock!.GetTimestamp() >= _deadline) Request("Expired");
        var error = ErrorCode;
        if (error is not null || Gate.IsRevoked) throw new OwnedLabException(error ?? "Cancelled");
    }
    internal ITimer CommandDeadline(TimeProvider clock, out long commandDeadline)
    {
        if (!_bound) throw new InvalidOperationException();
        var now = clock.GetTimestamp();
        var remaining = clock.GetElapsedTime(now, _deadline);
        if (remaining <= TimeSpan.Zero) { Request("Expired"); Check(); }
        commandDeadline = remaining <= TimeSpan.FromSeconds(10) ? _deadline : checked(now + clock.TimestampFrequency * 10);
        return clock.CreateTimer(_ => Request("Expired"), null, clock.GetElapsedTime(now, commandDeadline), Timeout.InfiniteTimeSpan);
    }
    internal async Task FinishAsync()
    {
        await _caller.DisposeAsync().ConfigureAwait(false);
        if (_lifetime is not null) await _lifetime.DisposeAsync().ConfigureAwait(false);
        while (true)
        {
            Task[] callbacks;
            lock (_sync)
            {
                callbacks = _callbacks.ToArray(); _callbacks.Clear();
                if (callbacks.Length == 0) { _finished = true; break; }
            }
            try { await Task.WhenAll(callbacks).ConfigureAwait(false); } catch (Exception) { /* Actual callback faults are observed after every callback retires. */ }
        }
        _source.Dispose();
    }
}
