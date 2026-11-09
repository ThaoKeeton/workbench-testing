using Workbench.Sdk;
namespace Workbench.Testing.Domain;

public sealed record CaseDraftView(string Id,string BaseRevision,long Version,long SavedVersion,bool IsDirty,bool IsSaving,string? ErrorCode,TestCaseSetContent Content);
public sealed record DraftRecoveryResult(TestCaseSetSnapshot Preserved,bool ReloadedOriginal);

/// <summary>One retained editor model and one coalesced CAS writer. Text capture is synchronous and has no UI dependencies.</summary>
public sealed class CaseDraftSession
{
    private readonly object _sync=new();
    private readonly IProjectTestWorkspace _testing;
    private TestCaseSetSnapshot _saved;
    private TestCaseSetContent _content;
    private long _version,_savedVersion;
    private Task? _writer;
    private string? _error;
    public CaseDraftSession(IProjectTestWorkspace testing,TestCaseSetSnapshot initial)
    { _testing=testing;_saved=ModelCopy.Set(initial);_content=ModelCopy.Content(new(initial.Name,initial.Cases)); }
    public event Action<CaseDraftView>? Changed;
    public CaseDraftView View
    {
        get { lock(_sync) return new(_saved.Id,_saved.Revision,_version,_savedVersion,_version!=_savedVersion,_writer is not null && _error is null && _version!=_savedVersion,_error,ModelCopy.Content(_content)); }
    }
    public void CaptureName(string name)=>Capture(content=>content.Name==name ? content : content with { Name=name });
    public void CaptureCaseMetadata(string caseId,string title,string prerequisites)=>Capture(content=>
    {
        var found=false;
        var cases=content.Cases.Select(c=>
        {
            if(c.Id!=caseId) return c;
            found=true;return c.Title==title && c.Prerequisites==prerequisites ? c : c with { Title=title,Prerequisites=prerequisites };
        }).ToArray();
        if(!found) throw DomainErrors.Error("InvalidId");
        return cases.SequenceEqual(content.Cases) ? content : content with { Cases=Array.AsReadOnly(cases) };
    });
    public void CaptureStepText(string caseId,string stepId,string instruction,string expected)=>Capture(content=>
    {
        var found=false;
        var cases=content.Cases.Select(c=> c.Id!=caseId ? c : c with { Steps=ModelCopy.List(c.Steps.Select(s=>
        {
            if(s.Id!=stepId) return s;
            found=true;return s.Instruction==instruction && s.Expected==expected ? s : s with { Instruction=instruction,Expected=expected,Action=null };
        })) }).ToArray();
        if(!found) throw DomainErrors.Error("InvalidId");
        var current=content.Cases.Single(c=>c.Id==caseId).Steps.Single(s=>s.Id==stepId);
        return current.Instruction==instruction && current.Expected==expected ? content : content with { Cases=Array.AsReadOnly(cases) };
    });
    private void Capture(Func<TestCaseSetContent,TestCaseSetContent> change)
    {
        TaskCompletionSource? start=null;
        lock(_sync)
        {
            var changed=change(_content);
            if(ReferenceEquals(changed,_content)) return;
            _content=changed;_version++;
            if(_writer is null) { _error=null;start=StartWriterLocked(); }
        }
        start?.TrySetResult();Notify();
    }
    private TaskCompletionSource StartWriterLocked()
    {
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer=WriteLoopAsync(start.Task); // Actual writer is retained before its IO continuation is released.
        return start;
    }
    private async Task WriteLoopAsync(Task admission)
    {
        await admission.ConfigureAwait(false);
        while(true)
        {
            TestCaseSetContent content;string revision,id;long version;
            lock(_sync)
            {
                if(_savedVersion==_version) { _writer=null;return; }
                content=_content;revision=_saved.Revision;id=_saved.Id;version=_version;
            }
            try
            {
                var saved=await _testing.UpdateCaseSetAsync(id,content,revision).ConfigureAwait(false);
                lock(_sync) { _saved=ModelCopy.Set(saved);_savedVersion=version;_error=null; }
                Notify();
            }
            catch(Exception error)
            {
                lock(_sync) _error=DomainErrors.Code(error);
                Notify();
                TaskCompletionSource? restart=null;
                lock(_sync)
                {
                    _writer=null;
                    if(_version>version) { _error=null;restart=StartWriterLocked(); }
                }
                restart?.TrySetResult();return;
            }
        }
    }
    private void Notify()
    {
        var snapshot=View;var handlers=Changed;if(handlers is null) return;
        foreach(Action<CaseDraftView> handler in handlers.GetInvocationList())
            try { handler(snapshot); } catch(Exception) { }
    }
    public async Task<TestCaseSetSnapshot> FlushAsync(CancellationToken cancellationToken=default)
    {
        while(true)
        {
            Task? writer;
            lock(_sync)
            {
                writer=_writer;
                if(writer is null)
                {
                    if(_version!=_savedVersion) throw DomainErrors.Error(_error ?? "TestingFailed");
                    cancellationToken.ThrowIfCancellationRequested();return ModelCopy.Set(_saved);
                }
            }
            // Cancellation never detaches a write already accepted by the editor.
            await writer.ConfigureAwait(false);
        }
    }
    public Task<TestCaseSetSnapshot> RetryFlushAsync(CancellationToken cancellationToken=default)
    {
        TaskCompletionSource? start=null;
        lock(_sync)
            if(_version!=_savedVersion && _writer is null) { _error=null;start=StartWriterLocked(); }
        start?.TrySetResult();return FlushAsync(cancellationToken);
    }
    public async Task<DraftRecoveryResult> PreserveAsNewAndReloadAsync(CancellationToken cancellationToken=default)
    {
        Task? writer;lock(_sync) writer=_writer;
        if(writer is not null) await writer.ConfigureAwait(false);
        TestCaseSetContent content;long version;string id;
        lock(_sync)
        {
            if(_version==_savedVersion || _error!="RevisionConflict") throw DomainErrors.Error("InvalidCaseSet");
            content=_content;version=_version;id=_saved.Id;
        }
        var preserved=await _testing.CreateCaseSetAsync(ModelCopy.Draft(content),cancellationToken).ConfigureAwait(false);
        var latest=await _testing.ReadCaseSetAsync(id,cancellationToken).ConfigureAwait(false);
        lock(_sync)
        {
            // A newer accepted edit must not be overwritten by this asynchronous recovery.
            if(_version!=version) return new(ModelCopy.Set(preserved),false);
            _saved=ModelCopy.Set(latest);_content=ModelCopy.Content(new(latest.Name,latest.Cases));_savedVersion=_version;_error=null;
        }
        Notify();return new(ModelCopy.Set(preserved),true);
    }
}
