using Workbench.Testing.Domain;
using Workbench.Testing.Domain.OwnedLab;
using System.Runtime.ExceptionServices;
using Workbench.Sdk;

namespace Workbench.Testing;

internal sealed record NativeTestingAdmission(bool UserActions, bool ActivityStarts);
internal sealed class NativeReopening(IDisposable owner, IDisposable? activity, IDisposable source, IDisposable owned)
{
    internal IDisposable Owner { get; } = owner;
    internal IDisposable? Activity { get; } = activity;
    internal IDisposable Source { get; } = source;
    internal IDisposable Owned { get; } = owned;
}

/// <summary>Fixed Testing-plugin barriers. Host hooks own these tasks; an owner action must never drain itself.</summary>
internal sealed class NativeTestingLifecycle(NativeActionOwner owner, NativeActivityCoordinator? activity,
    NativeRemoteOperation sourceOperation, NativeRemoteOperation ownedOperation, TestingController? virtualController,
    OwnedPilotController? pilot, Func<NativeReopening?, Task> renderLatest, Func<Task> retireUi)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _phase = new(1, 1);
    private Task? _leave, _stop;
    private bool _terminal;
    private IDisposable? _permanentPilotSeal;
    private NativeReopening? _reopening;

    internal NativeTestingAdmission ReadAdmission(NativeReopening? reopening = null)
    {
        NativeReopening? current;
        lock (_sync)
        {
            if (_terminal) return new(false, false);
            current = ReferenceEquals(reopening, _reopening) ? reopening : null;
        }
        var users = owner.CanAdmitForRender(current?.Owner) && virtualController?.Snapshot.IsStopped != true;
        return new(users, users && activity?.CanAdmitForRender(current?.Activity) == true
            && sourceOperation.CanAdmitForRender(current?.Source) && ownedOperation.CanAdmitForRender(current?.Owned));
    }

    internal Task RetainActivityAsync(Func<Task> transition) => owner.InternalAsync(async () =>
    {
        try { await transition().ConfigureAwait(false); }
        finally
        {
            // Always latest state, after actual reservation release. Never write this invocation's old status here.
            try { await renderLatest(null).ConfigureAwait(false); }
            catch (Exception) { /* Presentation cannot replace the original operation's outcome. */ }
        }
    });

    internal void SealLifetime()
    {
        lock (_sync)
        {
            if (_terminal) return;
            _terminal = true;
            owner.Terminal(); activity?.Terminal(); sourceOperation.Terminal(); ownedOperation.Terminal();
            // Install every other admission barrier before pilot sealing requests real revocation.
            _permanentPilotSeal = pilot?.SealAdmission();
        }
    }

    internal Task PrepareLeaveAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_terminal) return Task.FromException(new PluginTestingException("TestingContextClosed"));
            if (_leave is { IsCompleted: false }) return _leave;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _leave = completion.Task;
        }
        _ = CompleteAsync(completion, () => LeaveCoreAsync(cancellationToken));
        return completion.Task;
    }

    private async Task LeaveCoreAsync(CancellationToken token)
    {
        await _phase.WaitAsync().ConfigureAwait(false);
        var failures = new Failure();
        IDisposable? ownerSeal = null, activitySeal = null, sourceSeal = null, ownedSeal = null, pilotSeal = null;
        try
        {
            ownerSeal = owner.Seal(); activitySeal = activity?.Seal(); sourceSeal = sourceOperation.Seal(); ownedSeal = ownedOperation.Seal();
            pilotSeal = pilot?.SealAdmission();
            var cancellation = StartCancellation();
            foreach (var task in cancellation) await failures.JoinAsync(task).ConfigureAwait(false);
            await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
            if (activity is not null) await failures.JoinAsync(activity.DrainAsync()).ConfigureAwait(false);
            if (virtualController is not null) await failures.JoinAsync(Start(() => virtualController.PrepareLeaveAsync())).ConfigureAwait(false);
            if (pilot is not null) await failures.JoinAsync(Start(() => pilot.PrepareLeaveAsync())).ConfigureAwait(false);
        }
        catch (Exception error) { failures.Record(error); }
        finally
        {
            // Disposal publishes asynchronously; first join the producer pump, then its retained UI consumers.
            failures.Dispose(pilotSeal);
            if (pilot is not null) await failures.JoinAsync(Start(pilot.DrainNotificationsAsync)).ConfigureAwait(false);
            await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
            try
            {
                if (ownerSeal is not null && sourceSeal is not null && ownedSeal is not null)
                {
                    var ticket = new NativeReopening(ownerSeal, activitySeal, sourceSeal, ownedSeal);
                    lock (_sync) { if (!_terminal) _reopening = ticket; }
                    await failures.JoinAsync(owner.InternalAsync(() => renderLatest(ticket))).ConfigureAwait(false);
                    await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_sync) _reopening = null;
                failures.Dispose(ownedSeal); failures.Dispose(sourceSeal); failures.Dispose(activitySeal); failures.Dispose(ownerSeal);
                _phase.Release();
            }
        }
        failures.Throw(); token.ThrowIfCancellationRequested();
    }

    internal Task StopAsync(CancellationToken cancellationToken = default)
    {
        SealLifetime();
        TaskCompletionSource completion;
        Task? priorLeave;
        lock (_sync)
        {
            if (_stop is not null) return _stop;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = completion.Task;
            priorLeave = _leave;
        }
        // Capture actual entries now, even if an in-progress Leave still owns the phase semaphore.
        var cancellation = StartCancellation();
        _ = CompleteAsync(completion, () => StopCoreAsync(cancellation, priorLeave, cancellationToken));
        return completion.Task;
    }

    private async Task StopCoreAsync(Task[] cancellation, Task? priorLeave, CancellationToken token)
    {
        var failures = new Failure();
        if (priorLeave is not null) await failures.JoinAsync(priorLeave).ConfigureAwait(false);
        await _phase.WaitAsync().ConfigureAwait(false);
        try
        {
            var virtualStop = virtualController is null ? Task.CompletedTask : Start(() => virtualController.StopAsync());
            foreach (var task in cancellation) await failures.JoinAsync(task).ConfigureAwait(false);
            await failures.JoinAsync(virtualStop).ConfigureAwait(false);
            await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
            if (activity is not null) await failures.JoinAsync(activity.DrainAsync()).ConfigureAwait(false);
            if (pilot is not null) await failures.JoinAsync(Start(pilot.DrainNotificationsAsync)).ConfigureAwait(false);
            await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
            await failures.JoinAsync(owner.InternalAsync(retireUi)).ConfigureAwait(false);
            if (pilot is not null) await failures.JoinAsync(Start(pilot.DrainNotificationsAsync)).ConfigureAwait(false);
            await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
            // The page bridges and original decode work have retired while the journal is still owned.
            if (pilot is not null) await failures.JoinAsync(Start(() => pilot.StopAsync())).ConfigureAwait(false);
            await failures.JoinAsync(owner.DrainAsync()).ConfigureAwait(false);
        }
        finally
        {
            failures.Dispose(_permanentPilotSeal); _permanentPilotSeal = null;
            _phase.Release();
        }
        failures.Throw(); token.ThrowIfCancellationRequested();
    }

    private Task[] StartCancellation() =>
    [
        Start(sourceOperation.RequestCancellationAsync), Start(ownedOperation.RequestCancellationAndDrainAsync),
        pilot is null ? Task.CompletedTask : Start(pilot.CancelAndDrainAsync)
    ];
    private static Task Start(Func<Task> action)
    {
        try { return action(); }
        catch (Exception error) { return Task.FromException(error); }
    }
    private static async Task CompleteAsync(TaskCompletionSource completion, Func<Task> work)
    {
        try { await work().ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private sealed class Failure
    {
        private ExceptionDispatchInfo? _first;
        internal void Record(Exception error) => _first ??= ExceptionDispatchInfo.Capture(error);
        internal async Task JoinAsync(Task work)
        {
            try { await work.ConfigureAwait(false); }
            catch (Exception error) { Record(error); }
        }
        internal void Dispose(IDisposable? lease)
        {
            try { lease?.Dispose(); }
            catch (Exception error) { Record(error); }
        }
        internal void Throw() => _first?.Throw();
    }
}
