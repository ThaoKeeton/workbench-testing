using System.Runtime.ExceptionServices;
using Workbench.Sdk;

namespace Workbench.Testing;

internal sealed class NativeRemoteOperation
{
    private readonly object _sync = new();
    private Entry? _active;
    private int _seals;
    private bool _terminal;
    internal IDisposable Seal() { lock (_sync) _seals++; return new Lease(this); }
    internal void Terminal() { lock (_sync) _terminal = true; }
    private sealed class Lease(NativeRemoteOperation owner) : IDisposable
    {
        private NativeRemoteOperation? _owner = owner;
        internal bool IsFor(NativeRemoteOperation candidate) => ReferenceEquals(_owner, candidate);
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
    private sealed class Entry(Task completion)
    {
        internal readonly Task Completion = completion;
        internal readonly CancellationTokenSource Source = new();
        internal bool AcceptsCancellation = true;
        internal Task? Cancellation;
    }
    internal bool IsBusy { get { lock (_sync) return _active is not null; } }
    internal bool CanAdmitForRender(IDisposable? temporarySeal = null)
    {
        lock (_sync) return !_terminal && _active is null && _seals == (temporarySeal is Lease lease && lease.IsFor(this) ? 1 : 0);
    }
    internal Task RequestCancellationAndDrainAsync()
    {
        Task cancellation, completion;
        lock (_sync)
        {
            if (_active is not { } entry) return Task.CompletedTask;
            completion = entry.Completion;
            cancellation = CancelLocked(entry);
        }
        return JoinCapturedAsync();
        async Task JoinCapturedAsync()
        {
            ExceptionDispatchInfo? failure = null;
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            try { await completion.ConfigureAwait(false); }
            catch (Exception) { /* The original invocation retains its own result/failure. */ }
            failure?.Throw();
        }
    }
    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, Func<Exception?, Task>? settled = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(completion.Task);
        lock (_sync)
        {
            if (_terminal || _seals != 0 || _active is not null)
            { entry.Source.Dispose(); throw new PluginTestingException(_terminal ? "TestingContextClosed" : "StoreBusy"); }
            _active = entry;
        }
        _ = ExecuteAsync();
        return completion.Task;
        async Task ExecuteAsync()
        {
            T result = default!; Exception? failure = null;
            try { result = await action(entry.Source.Token).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            Task cancellation;
            lock (_sync)
            {
                // Close cancellation admission before taking the task to join; disposal uses the same lock.
                entry.AcceptsCancellation = false;
                cancellation = entry.Cancellation ?? Task.CompletedTask;
            }
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            try { if (settled is not null) await settled(failure).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            lock (_sync)
            {
                try { entry.Source.Dispose(); }
                catch (Exception error) { failure ??= error; }
                _active = null;
                if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
                else if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult(result);
                // Completion is the retained entry itself; no asynchronous postfix follows publication.
            }
        }
    }
    internal Task RequestCancellationAsync()
    {
        lock (_sync)
        {
            if (_active is not { } entry) return Task.CompletedTask;
            // CancelAsync schedules actual callbacks off the UI thread. The operation retains that exact task.
            return CancelLocked(entry);
        }
    }
    private static Task CancelLocked(Entry entry) => entry.AcceptsCancellation
        ? entry.Cancellation ??= entry.Source.CancelAsync() : entry.Cancellation ?? Task.CompletedTask;
}
