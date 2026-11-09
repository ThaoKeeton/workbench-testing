using System.Collections.Concurrent;

namespace Workbench.Core.Tests;

/// <summary>A dedicated, test-owned dispatcher; actual async filesystem work is not mocked.</summary>
internal sealed class TestDispatcher : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

    internal static Task<T> RunAsync<T>(Func<Task<T>> action, Action? betweenTurns = null)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var dispatcher = new TestDispatcher();
            SetSynchronizationContext(dispatcher);
            try
            {
                var operation = action();
                while (!operation.IsCompleted)
                {
                    betweenTurns?.Invoke();
                    if (!dispatcher._queue.TryTake(out var work, TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("The test dispatcher received no continuation.");
                    work.Callback(work.State);
                }
                result.TrySetResult(operation.GetAwaiter().GetResult());
            }
            catch (Exception error) { result.TrySetException(error); }
            finally { SetSynchronizationContext(null); }
        }) { IsBackground = true, Name = "Workbench test dispatcher" };
        thread.Start();
        return result.Task;
    }

    public void Dispose() => _queue.Dispose();
}
