using System.Runtime.ExceptionServices;

namespace Workbench.Testing.SourceAcceptance;

internal sealed class SourceAcceptanceOwnership(SourceAcceptanceScenarioLedger ledger,
    HttpMessageHandler figma, HttpMessageHandler ai, Func<CancellationToken, Task> stopInner)
{
    private readonly object _sync = new();
    private Task? _stop;
    internal Task StopAsync(CancellationToken cancellationToken = default)
    {
        ledger.SealCommands();
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_stop is not null) return _stop;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = completion.Task;
        }
        // The exact completion is retained before invoking even the synchronous inner prefix.
        _ = CompleteAsync(completion, cancellationToken);
        return completion.Task;
    }
    private async Task CompleteAsync(TaskCompletionSource completion, CancellationToken token)
    {
        ExceptionDispatchInfo? failure = null;
        try { await stopInner(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        try { await ledger.JoinReleaseMonitorsAsync().ConfigureAwait(false); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        try { figma.Dispose(); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        try { ai.Dispose(); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        try
        {
            failure?.Throw(); token.ThrowIfCancellationRequested(); completion.TrySetResult();
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
