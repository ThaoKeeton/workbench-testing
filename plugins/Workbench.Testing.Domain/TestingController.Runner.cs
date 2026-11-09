using Workbench.Sdk;
namespace Workbench.Testing.Domain;

public sealed partial class TestingController
{
    private bool CancellationPending(Attempt attempt,IPluginBackgroundExecution? execution=null)
    { lock(_sync) return attempt.CancelRequested || _closed || ContextClosed || execution?.CancellationToken.IsCancellationRequested==true; }
    private async Task RunAttemptAsync(Task admission,Attempt attempt,IPluginBackgroundExecution execution)
    {
        await admission.ConfigureAwait(false);
        IControlledTestSession? session=null;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token,execution.CancellationToken,_context.Lifetime);
        try
        {
            if(!ServiceOpen) return;
            if(CancellationPending(attempt,execution)) return;
            if(!await JournalAsync(attempt,current=>CancellationPending(attempt,execution) || current.State!=TestRunState.Queued ? null : new(TestRunEventKind.RunStarted)).ConfigureAwait(false)) return;
            if(_adapter.TargetId!=VirtualTaskFormAdapter.VirtualTargetId) throw DomainErrors.Error("TargetUnavailable");
            session=await _adapter.OpenAsync(attempt.Current.Plan.SessionId,linked.Token).ConfigureAwait(false);
            foreach(var item in attempt.Current.Plan.Cases)
            {
                if(CancellationPending(attempt,execution)) break;
                await session.ResetCaseAsync(item.Id,linked.Token).ConfigureAwait(false);
                foreach(var step in item.Steps)
                {
                    if(CancellationPending(attempt,execution)) break;
                    var started=await JournalAsync(attempt,current=>CancellationPending(attempt,execution) || current.State!=TestRunState.Running ? null : new(TestRunEventKind.StepStarted,item.Id,step.Id)).ConfigureAwait(false);
                    if(!started || CancellationPending(attempt,execution)) break;
                    var action=step.Action ?? throw DomainErrors.Error("ManualStepUnsupported");
                    // This exact typed action is invoked only after the StepStarted event AND head publication succeeded.
                    var result=await session.ExecuteAsync(action,linked.Token).ConfigureAwait(false);
                    if(!result.Passed) { lock(_sync) attempt.ObservedFailure=true; }
                    if(!ServiceOpen) break;
                    if(result.Passed && result.ErrorCode is not null) throw DomainErrors.Error("AdapterFailed");
                    var receipt=await _testing.AddRunEvidenceAsync(attempt.Current.Plan.Id,new("image/bmp",result.PassiveFrame,true)).ConfigureAwait(false);
                    await JournalAsync(attempt,_=>new(TestRunEventKind.StepFinished,item.Id,step.Id,result.Passed ? TestStepOutcome.Passed : TestStepOutcome.Failed,
                        result.Passed ? null : DomainErrors.Safe(result.ErrorCode ?? "AdapterFailed"),[receipt.Id]) { NativePlainTextState=result.NativePlainTextState }).ConfigureAwait(false);
                    if(!result.Passed) break;
                }
                lock(_sync) if(attempt.ObservedFailure) break;
            }
        }
        catch(OperationCanceledException) when(CancellationPending(attempt,execution)) { lock(_sync) attempt.CancelRequested=true; }
        catch(Exception error) { var code=DomainErrors.Code(error);lock(_sync) attempt.ExecutionError=code;SetError(code); }
        finally
        {
            // Disposal is actual owned adapter work. No terminal journal event is written before it settles.
            if(session is not null)
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch(Exception error) { var code=DomainErrors.Code(error);lock(_sync) attempt.ExecutionError=code;SetError(code); }
            lock(_sync)
            {
                if(execution.CancellationToken.IsCancellationRequested) attempt.CancelRequested=true;
                attempt.WorkSettled=true;
            }
            if(ServiceOpen) await TrySettleAsync(attempt).ConfigureAwait(false);
        }
    }
    private async Task<bool> TrySettleAsync(Attempt attempt)
    {
        if(!ServiceOpen) return false;
        try
        {
            await JournalAsync(attempt,current=>
            {
                if(DomainErrors.IsTerminal(current.State)) return null;
                bool failed,cancel,interrupted;string? error;
                lock(_sync) { failed=attempt.ObservedFailure || attempt.ExecutionError is not null;cancel=attempt.CancelRequested;interrupted=attempt.InterruptionRequested;error=attempt.ExecutionError; }
                if(!attempt.WorkSettled && attempt.StartSucceeded) return null;
                var kind=failed ? TestRunEventKind.Failed : cancel ? interrupted ? TestRunEventKind.Interrupted : TestRunEventKind.Cancelled : TestRunEventKind.Completed;
                return new(kind,ErrorCode:failed ? error : null);
            }).ConfigureAwait(false);
            lock(_sync) { attempt.TerminalError=null;return DomainErrors.IsTerminal(attempt.Current.State); }
        }
        catch(Exception error) { var code=DomainErrors.Code(error);lock(_sync) attempt.TerminalError=code;SetError(code);return false; }
    }
    private async Task<TestRunSnapshot?> CancelAttemptAsync(bool interrupted,bool writeCheckpoint)
    {
        var attempt=CurrentAttempt;if(attempt is null) return null;
        lock(_sync) { attempt.CancelRequested=true;attempt.InterruptionRequested|=interrupted; }
        string? requestError=null;
        if(writeCheckpoint && ServiceOpen)
            try
            {
                await JournalAsync(attempt,current=>DomainErrors.IsTerminal(current.State) || current.State==TestRunState.CancelRequested || current.Events.Any(e=>e.Outcome==TestStepOutcome.Failed)
                    ? null : new(TestRunEventKind.CancelRequested)).ConfigureAwait(false);
            }
            catch(Exception error) { requestError=DomainErrors.Code(error);SetError(requestError); }
        await attempt.Cancellation.CancelAsync().ConfigureAwait(false);
        await JoinRunWorkAsync(attempt).ConfigureAwait(false);
        lock(_sync) if(attempt.StartWork is null || !attempt.StartSucceeded) attempt.WorkSettled=true;
        if(writeCheckpoint && ServiceOpen)
        {
            var terminal=await TrySettleAsync(attempt).ConfigureAwait(false);
            await RetireAttemptAsync(attempt).ConfigureAwait(false);
            if(!terminal) { lock(_sync) requestError=attempt.TerminalError ?? requestError ?? "TestingFailed"; }
            else requestError=null;
        }
        if(requestError is not null) throw DomainErrors.Error(requestError);
        lock(_sync) return ModelCopy.Run(attempt.Current);
    }
    public Task<TestRunSnapshot?> CancelAsync(CancellationToken cancellationToken=default)=>Admit(async()=>
    { var result=await CancelAttemptAsync(false,true).ConfigureAwait(false);cancellationToken.ThrowIfCancellationRequested();return result; },false);
}
