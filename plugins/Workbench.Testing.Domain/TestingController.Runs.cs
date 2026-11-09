using Workbench.Sdk;
namespace Workbench.Testing.Domain;

public sealed partial class TestingController
{
    private sealed class Attempt(TestRunSnapshot run)
    {
        internal TestRunSnapshot Current=ModelCopy.Run(run);
        internal readonly SemaphoreSlim Journal=new(1,1);
        internal readonly CancellationTokenSource Cancellation=new();
        internal Task<PluginTaskOperationResult>? StartWork;
        internal Task? RunWork,RetirementWork;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool StartSucceeded,CancelRequested,InterruptionRequested,WorkSettled,Released,ObservedFailure;
        internal string? ExecutionError,TerminalError;
    }
    private Attempt? CurrentAttempt { get { lock(_sync) return _attempt; } }
    // The plugin holds its source/virtual reservation; independent Domain callers share no such guarantee.
    internal Task<bool> TryJoinPriorTerminalRunAsync()=>Admit(async()=>
    {
        var previous=CurrentAttempt;
        if(previous is null) return true;
        lock(_sync)
        {
            if(previous.Released) return true;
            if(!DomainErrors.IsTerminal(previous.Current.State)) return false; // Not an operation failure or cancellation request.
        }
        await JoinRunWorkAsync(previous).ConfigureAwait(false);
        await RetireAttemptAsync(previous).ConfigureAwait(false);
        lock(_sync) return previous.Released;
    },false);
    private void RecordRun(Attempt attempt,TestRunSnapshot run)
    {
        lock(_sync)
        {
            attempt.Current=ModelCopy.Run(run);
            if(_selectedRun is null || _selectedRun.Plan.Id==run.Plan.Id) _selectedRun=attempt.Current;
            var p=run.Plan;var summary=new TestRunSummary(p.Id,p.SessionId,p.ProjectId,p.ProducerPluginId,p.CreatedUtc,p.ParentRunId,p.CaseSetId,p.CaseRevision,run.State,run.Sequence,run.HeadHash);
            _runs=ModelCopy.List(_runs.Where(r=>r.Id!=p.Id).Append(summary).OrderBy(r=>r.Id,StringComparer.Ordinal));
        }
    }
    private async Task<bool> JournalAsync(Attempt attempt,Func<TestRunSnapshot,TestRunEventDraft?> next)
    {
        var changed=false;
        await attempt.Journal.WaitAsync().ConfigureAwait(false);
        try
        {
            if(!ServiceOpen) return false;
            TestRunSnapshot current;lock(_sync) current=attempt.Current;
            var draft=next(current);
            if(draft is null) return false;
            var committed=await _testing.AppendRunEventAsync(current.Plan.Id,current.Sequence,current.HeadHash,draft).ConfigureAwait(false);
            RecordRun(attempt,committed);changed=true;return true;
        }
        finally { attempt.Journal.Release();if(changed) Publish(); }
    }
    private async Task EnsureAttemptAvailableAsync()
    {
        var previous=CurrentAttempt;
        if(previous is null) return;
        TestRunState state;lock(_sync) state=previous.Current.State;
        if(!DomainErrors.IsTerminal(state)) throw DomainErrors.Error("InvalidRun");
        await JoinRunWorkAsync(previous).ConfigureAwait(false);
        await RetireAttemptAsync(previous).ConfigureAwait(false);
        lock(_sync) if(!previous.Released) throw DomainErrors.Error("StoreBusy");
    }
    private async Task<TestRunSnapshot> QueueRunCoreAsync(IReadOnlyList<string> selected,CancellationToken cancellationToken)
    {
        await EnsureAttemptAvailableAsync().ConfigureAwait(false);
        var set=await FlushDraftAsync(cancellationToken).ConfigureAwait(false);
        var run=await _testing.CreateRunAsync(set.Id,set.Revision,selected,cancellationToken).ConfigureAwait(false);
        var attempt=new Attempt(run);lock(_sync) { _attempt=attempt;_selectedRun=ModelCopy.Run(run);_error=null; }
        RecordRun(attempt,run);Publish();return ModelCopy.Run(run);
    }
    public Task<TestRunSnapshot> QueueRunAsync(IReadOnlyList<string> selectedCaseIds,CancellationToken cancellationToken=default)
        =>AdmitCaptured(()=>CaptureIds(selectedCaseIds,200),ids=>QueueRunCoreAsync(ids,cancellationToken));
    public Task<TestRunSnapshot> RunAsync(IReadOnlyList<string> selectedCaseIds,CancellationToken cancellationToken=default)
        =>AdmitCaptured(()=>CaptureIds(selectedCaseIds,200),async ids=> { await QueueRunCoreAsync(ids,cancellationToken).ConfigureAwait(false);return await StartQueuedCoreAsync(cancellationToken).ConfigureAwait(false); });
    private async Task<TestRunSnapshot> QueueRetryCoreAsync(string parentRunId,CancellationToken cancellationToken)
    {
        await EnsureAttemptAvailableAsync().ConfigureAwait(false);await FlushIfSelectedAsync().ConfigureAwait(false);
        var run=await _testing.CreateRetryRunAsync(parentRunId,cancellationToken).ConfigureAwait(false);
        var attempt=new Attempt(run);lock(_sync) { _attempt=attempt;_selectedRun=ModelCopy.Run(run);_error=null; }
        RecordRun(attempt,run);Publish();return ModelCopy.Run(run);
    }
    public Task<TestRunSnapshot> QueueRetryAsync(string parentRunId,CancellationToken cancellationToken=default)=>Admit(()=>QueueRetryCoreAsync(parentRunId,cancellationToken));
    public Task<TestRunSnapshot> RetryAsync(string parentRunId,CancellationToken cancellationToken=default)=>Admit(async()=>
    { await QueueRetryCoreAsync(parentRunId,cancellationToken).ConfigureAwait(false);return await StartQueuedCoreAsync(cancellationToken).ConfigureAwait(false); });
    public Task<TestRunSnapshot> StartQueuedRunAsync(CancellationToken cancellationToken=default)=>Admit(()=>StartQueuedCoreAsync(cancellationToken));
    private async Task<TestRunSnapshot> StartQueuedCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt=CurrentAttempt ?? throw DomainErrors.Error("InvalidRun");
        TaskCompletionSource? start=null;Task<PluginTaskOperationResult> work;
        lock(_sync)
        {
            if(attempt.Current.State!=TestRunState.Queued || attempt.CancelRequested) throw DomainErrors.Error("InvalidRun");
            if(attempt.StartWork is null || !attempt.StartSucceeded && attempt.StartWork.IsCompleted)
            {
                start=new(TaskCreationOptions.RunContinuationsAsynchronously);
                // Retain the actual host start BEFORE it can invoke a synchronous persistence/plugin prefix.
                attempt.StartWork=StartHostAsync(start.Task,attempt);
            }
            work=attempt.StartWork;
        }
        start?.TrySetResult();await work.ConfigureAwait(false);
        lock(_sync) return ModelCopy.Run(attempt.Current); // Only committed events, never host task status as run progress.
    }
    private async Task<PluginTaskOperationResult> StartHostAsync(Task admission,Attempt attempt)
    {
        await admission.ConfigureAwait(false);
        PluginTaskOperationResult result;
        try { result=await _context.BackgroundTasks.StartAsync(BackgroundTaskId).ConfigureAwait(false); }
        catch(Exception error) { SetError(DomainErrors.Code(error));result=new(false,"Testing operation failed."); }
        lock(_sync) attempt.StartSucceeded=result.Succeeded;
        if(!result.Succeeded) SetError("TestingFailed");
        Publish();return result;
    }
    private async Task RunSupervisedAsync(IPluginBackgroundExecution execution)
    {
        Attempt? attempt;Task? work;TaskCompletionSource? start=null;
        lock(_sync)
        {
            attempt=_attempt;
            if(attempt is null || attempt.StartWork is null || attempt.RunWork is not null) return;
            start=new(TaskCreationOptions.RunContinuationsAsynchronously);
            work=RunAttemptAsync(start.Task,attempt,execution);attempt.RunWork=work;
        }
        attempt.Entered.TrySetResult();start.TrySetResult();await work.ConfigureAwait(false);
    }
    private async Task JoinRunWorkAsync(Attempt attempt)
    {
        Task<PluginTaskOperationResult>? start;lock(_sync) start=attempt.StartWork;
        if(start is null) return;
        await start.ConfigureAwait(false);
        bool succeeded;lock(_sync) succeeded=attempt.StartSucceeded;
        if(!succeeded) return;
        // Successful host Start can precede delegate entry. Never infer settlement from a null RunWork.
        await attempt.Entered.Task.ConfigureAwait(false);
        Task actual;lock(_sync) actual=attempt.RunWork!;
        await actual.ConfigureAwait(false);
    }
    private Task RetireAttemptAsync(Attempt attempt)
    {
        TaskCompletionSource start;Task work;
        lock(_sync)
        {
            if(attempt.RetirementWork is not null) return attempt.RetirementWork;
            if(attempt.Released || !DomainErrors.IsTerminal(attempt.Current.State)) return Task.CompletedTask;
            start=new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Every old waiter joins this actual operation before the shared task ID can be reused.
            work=RetireAttemptCoreAsync(start.Task,attempt);attempt.RetirementWork=work;
        }
        start.TrySetResult();return work;
    }
    private async Task RetireAttemptCoreAsync(Task admission,Attempt attempt)
    {
        await admission.ConfigureAwait(false);
        await JoinRunWorkAsync(attempt).ConfigureAwait(false);
        bool started;lock(_sync) started=attempt.StartSucceeded;
        while(started && ServiceOpen)
        {
            // Also join the real supervisor's callback postfix before a later Start can be accepted as "already running".
            await _context.BackgroundTasks.StopAsync(BackgroundTaskId).ConfigureAwait(false);
            if(!ServiceOpen) break;
            var task=_context.BackgroundTasks.GetSnapshots().Single(s=>s.Id==BackgroundTaskId);
            // A finite host Stop can report pending; its actual work still belongs to this retirement.
            if(task.State is not (PluginBackgroundTaskState.Running or PluginBackgroundTaskState.Stopping)) break;
        }
        lock(_sync) attempt.Released=true;
        Publish();
    }
    public Task<TestRunSnapshot?> WaitForRunSettlementAsync(CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        var attempt=CurrentAttempt;if(attempt is null) return null;
        await JoinRunWorkAsync(attempt).ConfigureAwait(false);await RetireAttemptAsync(attempt).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();lock(_sync) return ModelCopy.Run(attempt.Current);
    },false);
    public Task<TestRunSnapshot> SelectRunAsync(string runId,CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        var run=await _testing.ReadRunAsync(runId,cancellationToken).ConfigureAwait(false);lock(_sync) _selectedRun=ModelCopy.Run(run);Publish();return ModelCopy.Run(run);
    });
    public Task<TestRunSnapshot?> ReadSelectedRunAsync(CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        string? id;lock(_sync) id=_selectedRun?.Plan.Id;if(id is null) return null;
        var run=await _testing.ReadRunAsync(id,cancellationToken).ConfigureAwait(false);
        lock(_sync) _selectedRun=ModelCopy.Run(run);Publish();return ModelCopy.Run(run);
    },false);
    public Task<TestEvidence> ReadEvidenceAsync(string runId,string evidenceId,CancellationToken cancellationToken=default)
        =>Admit(()=>_testing.ReadRunEvidenceAsync(runId,evidenceId,cancellationToken),false);
}
