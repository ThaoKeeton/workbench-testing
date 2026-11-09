using System.Collections.ObjectModel;
using Workbench.Sdk;
using Workbench.Testing.Domain;

namespace Workbench.Testing;

internal enum NativeActivityRefusal { Busy, Closed }
internal sealed class NativeActivityAdmissionException(NativeActivityRefusal reason) : Exception("Testing activity admission refused.")
{
    internal string ErrorCode => reason == NativeActivityRefusal.Busy ? "StoreBusy" : "TestingContextClosed";
    internal PluginTestingException ToTestingException() => new(ErrorCode);
}
internal sealed class NativeActivityCoordinator(TestingController controller, NativeRemoteOperation remote)
{
    private readonly object _sync = new();
    private Task? _active;
    private bool _busy, _terminal;
    private int _seals;
    internal bool IsBusy { get { lock (_sync) return _busy; } }
    internal bool CanAdmit => CanAdmitForRender();
    internal bool CanAdmitForRender(IDisposable? temporarySeal = null)
    {
        lock (_sync) return !_terminal && _active is null && _seals == (temporarySeal is Lease lease && lease.IsFor(this) ? 1 : 0);
    }
    internal IDisposable Seal() { lock (_sync) _seals++; return new Lease(this); }
    internal void Terminal() { lock (_sync) _terminal = true; }
    private sealed class Lease(NativeActivityCoordinator owner) : IDisposable
    {
        private NativeActivityCoordinator? _owner = owner;
        internal bool IsFor(NativeActivityCoordinator candidate) => ReferenceEquals(_owner, candidate);
        public void Dispose()
        {
            var current = Volatile.Read(ref _owner);
            if (current is null) return;
            lock (current._sync)
            {
                if (_owner is null) return;
                _owner = null; current._seals--;
            }
        }
    }
    internal async Task DrainAsync()
    {
        while (true)
        {
            Task? actual; lock (_sync) actual = _active;
            if (actual is null) return;
            try { await actual.ConfigureAwait(false); }
            catch (Exception) { /* The admitted caller observes its operation failure; drain still joins it. */ }
        }
    }
    internal Task<T> VirtualAsync<T>(Func<Task<T>> action, Func<Task> settled) => Admit(action, settled);
    internal Task<TResult> SourceAsync<TInput, TResult>(Func<TInput> capture, Func<TInput, Task<TResult>> action, Func<Task> settled)
        => Admit(async () =>
        {
            var input = capture();
            if (!await controller.TryJoinPriorTerminalRunAsync().ConfigureAwait(false)) throw new NativeActivityAdmissionException(NativeActivityRefusal.Busy);
            return await action(input).ConfigureAwait(false);
        }, settled);
    internal Task<TResult> RemoteSourceAsync<TInput, TResult>(Func<TInput> capture, Func<TInput, CancellationToken, Task<TResult>> action,
        Func<Exception?, Task> remoteSettled, Func<Task> finalNotify)
        => CancellableSourceAsync(remote, capture, null, action, remoteSettled, finalNotify);
    internal Task<TResult> OwnedAsync<TInput, TResult>(NativeRemoteOperation operation, Func<TInput> capture, Func<Task> admitted,
        Func<TInput, CancellationToken, Task<TResult>> action, Func<Exception?, Task> operationSettled, Func<Task> finalNotify)
        => CancellableSourceAsync(operation, capture, admitted, action, operationSettled, finalNotify);
    private Task<TResult> CancellableSourceAsync<TInput, TResult>(NativeRemoteOperation operation, Func<TInput> capture, Func<Task>? admitted,
        Func<TInput, CancellationToken, Task<TResult>> action, Func<Exception?, Task> operationSettled, Func<Task> finalNotify)
        => Admit(() =>
        {
            var entered = false;
            try
            {
                // Install the one existing CTS before caller capture or the first handoff await.
                return operation.RunAsync(async token =>
                {
                    entered = true;
                    var input = capture();
                    var handoff = controller.TryJoinPriorTerminalRunAsync();
                    if (admitted is not null) await NotifySafelyAsync(admitted).ConfigureAwait(false);
                    if (!await handoff.ConfigureAwait(false)) throw new NativeActivityAdmissionException(NativeActivityRefusal.Busy);
                    token.ThrowIfCancellationRequested();
                    return await action(input, token).ConfigureAwait(false);
                }, failure => NotifySafelyAsync(() => operationSettled(failure)));
            }
            catch (PluginTestingException error) when (!entered && error.ErrorCode is "StoreBusy" or "TestingContextClosed")
            {
                throw new NativeActivityAdmissionException(error.ErrorCode == "TestingContextClosed" ? NativeActivityRefusal.Closed : NativeActivityRefusal.Busy);
            }
        }, finalNotify);
    private Task<T> Admit<T>(Func<Task<T>> action, Func<Task> settled)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_terminal || _seals != 0 || _active is not null)
                return Task.FromException<T>(new NativeActivityAdmissionException(_terminal ? NativeActivityRefusal.Closed : NativeActivityRefusal.Busy));
            _active = completion.Task; _busy = true;
        }
        // Invoke outside the lock, without an async start latch: real UI capture must happen before return.
        _ = ExecuteAsync();
        return completion.Task;
        async Task ExecuteAsync()
        {
            T result = default!; Exception? failure = null;
            try { result = await action().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            lock (_sync) _busy = false; // Informational idle; the actual reservation still excludes reentry.
            await NotifySafelyAsync(settled).ConfigureAwait(false);
            lock (_sync)
            {
                if (failure is null) completion.TrySetResult(result);
                else completion.TrySetException(failure);
                _active = null; // No asynchronous tail follows release.
            }
        }
    }
    private static async Task NotifySafelyAsync(Func<Task> notification)
    {
        try { await notification().ConfigureAwait(false); }
        catch (Exception) { /* Observe a presentation fault without replacing the actual operation's outcome/failure. */ }
    }
}
internal static class NativeActivityInputs
{
    internal static NativeFigmaInput Figma(Func<NativeFigmaInput> capture)
    {
        var input = capture();
        var request = input.Request with { NodeIds = Array.AsReadOnly(input.Request.NodeIds.ToArray()) };
        var criteria = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in input.Criteria) criteria.Add(entry.Key, Array.AsReadOnly(entry.Value.ToArray()));
        return new(request, new ReadOnlyDictionary<string, IReadOnlyList<string>>(criteria));
    }
    internal static IReadOnlyList<string> Selected(Func<IReadOnlyList<string>> capture) => Array.AsReadOnly(capture().ToArray());
}
