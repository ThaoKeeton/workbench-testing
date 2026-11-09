using Workbench.Sdk;
using Workbench.Testing.Domain.Sources.Figma;
using Workbench.Testing.Domain.Generation.Ai;
namespace Workbench.Testing.Domain;

/// <summary>Portable authoring and one explicitly started supervised virtual attempt. Entry owns its one before-leave hook.</summary>
public sealed partial class TestingController
{
    public const string BackgroundTaskId="testing.virtual-run";
    private readonly object _sync=new();
    private readonly SemaphoreSlim _actions=new(1,1);
    private readonly HashSet<Task> _operations=[];
    private readonly HashSet<Task> _captures=[],_notifications=[];
    private Task? _notificationWork;
    private TestingControllerSnapshot? _pendingNotification;
    private Action<TestingControllerSnapshot>? _pendingHandlers;
    private bool _authoringBusy;
    private readonly IPluginTestingContext _context;
    private readonly IProjectTestWorkspace _testing;
    private readonly IControlledTestAdapter _adapter;
    private readonly FigmaFileReader _figmaReader;
    private readonly AiTestCaseGenerator _aiGenerator;
    private readonly string _projectId;
    private Task? _initialization,_preparation,_stop;
    private bool _initialized,_preparing,_preparationAdmission,_closed;
    private CaseDraftSession? _draft;
    private Attempt? _attempt;
    private TestRunSnapshot? _selectedRun;
    private IReadOnlyList<TestSourceSummary> _sources=[];
    private IReadOnlyList<TestCaseSetSummary> _sets=[];
    private IReadOnlyList<TestRunSummary> _runs=[];
    private string? _error;

    public TestingController(IPluginTestingContext context,IControlledTestAdapter? adapter=null)
        :this(context,adapter,new FigmaFileReader()) { }
    public TestingController(IPluginTestingContext context,IControlledTestAdapter? adapter,FigmaFileReader reader)
        :this(context,adapter,reader,new AiTestCaseGenerator()) { }
    public TestingController(IPluginTestingContext context,IControlledTestAdapter? adapter,FigmaFileReader figmaReader,AiTestCaseGenerator aiGenerator)
    { _context=context;_testing=context.Testing;_projectId=context.Project.Identity.Id;_adapter=adapter ?? new VirtualTaskFormAdapter();_figmaReader=figmaReader;_aiGenerator=aiGenerator; }
    public event Action<TestingControllerSnapshot>? Changed;
    public TestingControllerSnapshot Snapshot
    {
        get { lock(_sync) return new(_projectId,ModelCopy.List(_sources),ModelCopy.List(_sets),ModelCopy.List(_runs),_draft?.View,
            _selectedRun is null ? null : ModelCopy.Run(_selectedRun),_error,_preparing,_closed || ContextClosed,_attempt is { Released:false }) { IsAuthoringBusy=_authoringBusy }; }
    }
    private bool ContextClosed=>_context.StopReason!=PluginStopReason.None || _context.Lifetime.IsCancellationRequested;
    private bool ServiceOpen { get { lock(_sync) return !_closed && !ContextClosed; } }
    private void CheckAdmissionLocked()
    {
        if(_closed || ContextClosed) throw DomainErrors.Error("TestingContextClosed");
        if(_preparationAdmission) throw DomainErrors.Error("StoreBusy");
    }
    private Task<T> Admit<T>(Func<Task<T>> operation,bool serialized=true,Task? capture=null)
    {
        TaskCompletionSource start;Task<T> work;
        lock(_sync)
        {
            try { CheckAdmissionLocked(); } catch(Exception error) { return Task.FromException<T>(error); }
            start=new(TaskCreationOptions.RunContinuationsAsynchronously);
            work=ExecuteOwnedAsync(start.Task,operation,serialized,true,capture);_operations.Add(work);
        }
        Observe(work);start.TrySetResult();return work;
    }
    private Task<TResult> AdmitCaptured<TInput,TResult>(Func<TInput> capture,Func<TInput,Task<TResult>> operation)
    {
        var captured=new TaskCompletionSource<TInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work=Admit(async()=> { var input=await captured.Task.ConfigureAwait(false);return await operation(input).ConfigureAwait(false); },capture:captured.Task);
        if(work.IsCompleted) return work; // Closed admission must not evaluate caller collections.
        try { captured.TrySetResult(capture()); }
        catch(Exception error) { captured.TrySetException(DomainErrors.Error(DomainErrors.Code(error))); }
        return work;
    }
    private async Task<T> ExecuteOwnedAsync<T>(Task admission,Func<Task<T>> operation,bool serialized,bool requireInitialization,Task? capture=null)
    {
        await admission.ConfigureAwait(false);
        var acquired=false;
        try
        {
            if(requireInitialization)
            {
                Task? initialization;lock(_sync) initialization=_initialization;
                if(initialization is null) { if(capture is not null) await capture.ConfigureAwait(false);throw DomainErrors.Error("TestingFailed"); }
                await Task.WhenAll(initialization,capture ?? Task.CompletedTask).ConfigureAwait(false);
            }
            if(serialized)
            {
                await _actions.WaitAsync().ConfigureAwait(false);acquired=true;
                Task[] captures;lock(_sync) { _authoringBusy=true;captures=_captures.ToArray(); }
                Publish();await JoinObservedAsync(captures).ConfigureAwait(false);
            }
            if(!ServiceOpen) throw DomainErrors.Error("TestingContextClosed");
            return await operation().ConfigureAwait(false);
        }
        catch(OperationCanceledException) { throw new OperationCanceledException(); }
        catch(Exception error) { var code=DomainErrors.Code(error);SetError(code);throw DomainErrors.Error(code); }
        finally
        {
            if(acquired) { lock(_sync) _authoringBusy=false;_actions.Release(); }
            Publish();
        }
    }
    private void Observe(Task work)
    {
        _=work.ContinueWith(done=> { _=done.Exception;lock(_sync) { _operations.Remove(done);_captures.Remove(done);_notifications.Remove(done); } },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    private void CaptureEdit(Action<CaseDraftSession> capture)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);CaseDraftSession draft;
        lock(_sync)
        {
            CheckAdmissionLocked();
            if(_authoringBusy) throw DomainErrors.Error("StoreBusy");
            if(!_initialized || _draft is null) throw DomainErrors.Error("InvalidCaseSet");
            draft=_draft;_operations.Add(completion.Task);_captures.Add(completion.Task);
        }
        Observe(completion.Task);
        try { capture(draft);Publish(); }
        catch(Exception error) { var code=DomainErrors.Code(error);SetError(code);throw DomainErrors.Error(code); }
        finally { completion.TrySetResult(); }
    }
    private void SetError(string? code) { lock(_sync) _error=code;Publish(); }
    private void Publish()
    {
        TaskCompletionSource? start=null;Task? actual=null;
        lock(_sync)
        {
            if(_closed || Changed is null) return;
            _pendingNotification=Snapshot;_pendingHandlers=Changed;
            if(_notificationWork is null)
            {
                start=new(TaskCreationOptions.RunContinuationsAsynchronously);
                actual=NotifyAsync(start.Task);_notificationWork=actual;_notifications.Add(actual);
            }
        }
        if(actual is not null) { Observe(actual);start!.TrySetResult(); }
    }
    private async Task NotifyAsync(Task admission)
    {
        await admission.ConfigureAwait(false);
        while(true)
        {
            TestingControllerSnapshot? snapshot;Action<TestingControllerSnapshot>? handlers;
            lock(_sync)
            {
                if(_closed) { _pendingNotification=null;_pendingHandlers=null; }
                snapshot=_pendingNotification;handlers=_pendingHandlers;_pendingNotification=null;_pendingHandlers=null;
                if(snapshot is null) { _notificationWork=null;return; }
            }
            // One actual retained pump, one latest pending snapshot: a held consumer cannot accumulate draft history.
            // Plain-model callbacks run outside Core/controller locks. Entry owns any asynchronous UI dispatch.
            if(handlers is not null)
                foreach(Action<TestingControllerSnapshot> handler in handlers.GetInvocationList())
                    try { handler(snapshot); } catch(Exception) { }
        }
    }
    private async Task DrainNotificationsAsync()
    {
        while(true)
        {
            Task[] notifications;lock(_sync) notifications=_notifications.ToArray();
            if(notifications.Length==0) return;
            await JoinObservedAsync(notifications).ConfigureAwait(false);
            lock(_sync) foreach(var actual in notifications) if(actual.IsCompleted) _notifications.Remove(actual);
        }
    }
    private static string[] CaptureIds(IReadOnlyList<string> ids,int max)
    {
        var count=ids.Count;if(count==0) throw DomainErrors.Error("InvalidId");if(count>max) throw DomainErrors.Error("StoreLimitExceeded");
        var result=new string[count];for(var i=0;i<count;i++) result[i]=ids[i];return result;
    }
    private static async Task JoinObservedAsync(IEnumerable<Task> tasks)
    { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch(Exception) { /* Each owned operation retains its fixed error and is observed. */ } }

    public Task InitializeAsync(CancellationToken cancellationToken=default)
    {
        TaskCompletionSource start;Task<int> work;
        lock(_sync)
        {
            if(_initialization is not null) return _initialization;
            try { CheckAdmissionLocked(); } catch(Exception error) { return Task.FromException(error); }
            start=new(TaskCreationOptions.RunContinuationsAsynchronously);
            work=ExecuteOwnedAsync(start.Task,async()=>
            {
                // The real captured-owner barrier must finish before contributing a task or future native view.
                var sources=await _testing.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
                var sets=await _testing.ListCaseSetsAsync(cancellationToken).ConfigureAwait(false);
                var runs=await _testing.ListRunsAsync(cancellationToken).ConfigureAwait(false);
                if(!ServiceOpen) throw DomainErrors.Error("TestingContextClosed");
                _context.BackgroundTasks.Register(new(BackgroundTaskId,"Run simulated task form",RunSupervisedAsync) { RequiresPluginStart=true });
                lock(_sync) { _sources=ModelCopy.List(sources);_sets=ModelCopy.List(sets);_runs=ModelCopy.List(runs);_initialized=true; }
                Publish();return 0;
            },true,false);
            _initialization=work;_operations.Add(work);
        }
        Observe(work);start.TrySetResult();return work;
    }
    public Task<TestingControllerSnapshot> RefreshAsync(CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        var sources=await _testing.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        var sets=await _testing.ListCaseSetsAsync(cancellationToken).ConfigureAwait(false);
        var runs=await _testing.ListRunsAsync(cancellationToken).ConfigureAwait(false);
        string? selected;lock(_sync) selected=_selectedRun?.Plan.Id;
        var run=selected is null ? null : await _testing.ReadRunAsync(selected,cancellationToken).ConfigureAwait(false);
        lock(_sync) { _sources=ModelCopy.List(sources);_sets=ModelCopy.List(sets);_runs=ModelCopy.List(runs);if(run is not null) _selectedRun=ModelCopy.Run(run); }
        Publish();return Snapshot;
    },false);
}
