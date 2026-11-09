using Workbench.Testing.Domain.OwnedLab;

namespace Workbench.Testing;

internal sealed class NativeOwnedPilot(OwnedPilotController pilot, NativeActivityCoordinator activity, NativeRemoteOperation operation)
{
    private bool _pending;
    internal bool IsBusy => operation.IsBusy;
    internal bool IsPendingHandoff => Volatile.Read(ref _pending);
    internal Task<OwnedRunSnapshot> RunAsync(Func<OwnedScenario> capture, Func<Task> notify) => Start(capture, pilot.RunAsync, notify);
    internal Task<OwnedRunSnapshot> RetryAsync(Func<string> capture, Func<Task> notify) => Start(capture, pilot.RetryAsync, notify);
    private Task<OwnedRunSnapshot> Start<T>(Func<T> capture, Func<T, CancellationToken, Task<OwnedRunSnapshot>> run, Func<Task> notify)
        => activity.OwnedAsync(operation, () =>
        {
            var input = capture(); Volatile.Write(ref _pending, true); return input;
        }, notify, (input, token) =>
        {
            Volatile.Write(ref _pending, false); return run(input, token);
        }, _ =>
        {
            Volatile.Write(ref _pending, false); return notify();
        }, notify);
    internal Task CancelAsync() => operation.RequestCancellationAndDrainAsync();
}
