using Workbench.Sdk;

namespace Workbench.Testing;

/// <summary>Retains actual callback completion before invoking its synchronous prefix.
/// Seals affect new user work only; controller notifications remain owned through preparation.</summary>
internal sealed class NativeActionOwner
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _work = [];
    private int _seals;
    private bool _terminal;
    internal bool AdmitsUsers { get { lock (_sync) return !_terminal && _seals == 0; } }
    internal bool CanAdmitForRender(IDisposable? temporarySeal = null)
    {
        lock (_sync) return !_terminal && _seals == (temporarySeal is SealLease lease && lease.IsFor(this) ? 1 : 0);
    }
    internal IDisposable Seal() { lock (_sync) _seals++; return new SealLease(this); }
    internal void Terminal() { lock (_sync) _terminal = true; }
    private sealed class SealLease(NativeActionOwner owner) : IDisposable
    {
        private NativeActionOwner? _owner = owner;
        internal bool IsFor(NativeActionOwner candidate) => ReferenceEquals(_owner, candidate);
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
    internal Task UserAsync(Func<Task> action) => UserAsync(async () => { await action(); return true; });
    internal Task<T> UserAsync<T>(Func<Task<T>> action) => Admit(action, user: true);
    internal Task InternalAsync(Func<Task> action) => Admit(async () => { await action(); return true; }, user: false);
    internal Task HandleUserAsync(Func<Task> action, Action enter, Action settle, Action<Exception> failed) => UserAsync(async () =>
    {
        try { enter(); await action(); }
        catch (Exception error) { failed(error); }
        finally
        {
            try { settle(); }
            catch (Exception error) { failed(error); }
        }
    });
    private Task<T> Admit<T>(Func<Task<T>> action, bool user)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (user && (_terminal || _seals != 0))
                return Task.FromException<T>(new PluginTestingException(_terminal ? "TestingContextClosed" : "StoreBusy"));
            _work.Add(completion.Task);
        }
        // No delegate, control access or enumeration while holding the owner lock.
        _ = InvokeAsync();
        return completion.Task;
        async Task InvokeAsync()
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { lock (_sync) _work.Remove(completion.Task); }
        }
    }
    internal async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_sync) pending = _work.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception) { /* Each actual caller observes its own failure. Drain still joins every callback. */ }
        }
    }
}
