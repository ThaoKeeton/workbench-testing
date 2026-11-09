using Workbench.Sdk;
namespace Workbench.Testing.Domain;

public sealed partial class TestingController
{
    public Task PrepareLeaveAsync(CancellationToken cancellationToken=default)
    {
        TaskCompletionSource start;Task work;Task[] pending;
        lock(_sync)
        {
            if(_closed || ContextClosed) return Task.FromException(DomainErrors.Error("TestingContextClosed"));
            if(_preparation is { IsCompleted:false }) return _preparation;
            _preparing=true;_preparationAdmission=true;pending=_operations.ToArray();start=new(TaskCreationOptions.RunContinuationsAsynchronously);
            work=PrepareCoreAsync(start.Task,pending,cancellationToken);_preparation=work;
        }
        Observe(work);start.TrySetResult();Publish();return work;
    }
    private async Task PrepareCoreAsync(Task admission,Task[] pending,CancellationToken cancellationToken)
    {
        await admission.ConfigureAwait(false);
        try
        {
            var cancellation=CancelAttemptAsync(true,true);
            await Task.WhenAll(JoinObservedAsync(pending),cancellation).ConfigureAwait(false);
            if(!ServiceOpen) throw DomainErrors.Error("TestingContextClosed");
            await CancelAttemptAsync(true,true).ConfigureAwait(false);
            await FlushIfSelectedAsync().ConfigureAwait(false);
            await DrainNotificationsAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch(OperationCanceledException) { throw new OperationCanceledException(); }
        catch(Exception error) { var code=DomainErrors.Code(error);SetError(code);throw DomainErrors.Error(code); }
        finally
        {
            lock(_sync) _preparing=false;
            Publish();await DrainNotificationsAsync().ConfigureAwait(false);
            lock(_sync) _preparationAdmission=false; // No asynchronous tail follows reopening private actions.
        }
    }
    public Task StopAsync(CancellationToken cancellationToken=default)
    {
        TaskCompletionSource start;Task work;Task[] pending;
        lock(_sync)
        {
            if(_stop is not null) return _stop;
            _closed=true;pending=_operations.Concat(_notifications).Concat(_preparation is null ? [] : new[] { _preparation }).ToArray();
            start=new(TaskCreationOptions.RunContinuationsAsynchronously);work=StopCoreAsync(start.Task,pending,cancellationToken);_stop=work;
        }
        Observe(work);start.TrySetResult();Publish();return work;
    }
    private async Task StopCoreAsync(Task admission,Task[] pending,CancellationToken cancellationToken)
    {
        await admission.ConfigureAwait(false);
        // Terminal Stop revokes admission and joins actual work. Services are already closed by the host; no fresh checkpoint.
        await CancelAttemptAsync(false,false).ConfigureAwait(false);
        await JoinObservedAsync(pending).ConfigureAwait(false);
        await CancelAttemptAsync(false,false).ConfigureAwait(false); // A previously admitted queue/start may only now have published its attempt.
        CaseDraftSession? draft;lock(_sync) draft=_draft;
        if(draft is not null) await JoinObservedAsync([draft.FlushAsync()]).ConfigureAwait(false);
        lock(_sync) { if(_attempt is not null) _attempt.Released=true; }
        Publish();cancellationToken.ThrowIfCancellationRequested();
    }
}
