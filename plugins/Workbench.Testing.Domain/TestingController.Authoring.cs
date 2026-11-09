using Workbench.Sdk;
using Workbench.Testing.Domain.Sources;
namespace Workbench.Testing.Domain;

public sealed partial class TestingController
{
    public Task<TestSourceSnapshot> ImportAsync(TestSourceKind kind,ReadOnlyMemory<byte> bytes,CancellationToken cancellationToken=default)
        =>AdmitCaptured(()=> { if(bytes.Length>1024*1024) throw DomainErrors.Error("StoreLimitExceeded");return bytes.ToArray(); },original=>ImportCoreAsync(kind,original,cancellationToken));
    public Task<TestSourceSnapshot> ImportExampleAsync(TestSourceKind kind,CancellationToken cancellationToken=default)
        =>Admit(()=>ImportCoreAsync(kind,SyntheticTestingExamples.Read(kind),cancellationToken));
    private async Task<TestSourceSnapshot> ImportCoreAsync(TestSourceKind kind,byte[] original,CancellationToken cancellationToken)
    {
        ITestSourceProvider provider=kind switch { TestSourceKind.Requirements=>new RequirementSourceProvider(),TestSourceKind.Figma=>new FigmaSnapshotProvider(),_=>throw DomainErrors.Error("InvalidTestSource") };
        var saved=await _testing.PublishSourceAsync(provider.Parse(original),cancellationToken).ConfigureAwait(false);
        var sources=await _testing.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        lock(_sync) { _sources=ModelCopy.List(sources);_error=null; } Publish();return saved;
    }
    private CaseDraftSession SelectedDraft { get { lock(_sync) return _draft ?? throw DomainErrors.Error("InvalidCaseSet"); } }
    private async Task<TestCaseSetSnapshot> FlushDraftAsync(CancellationToken cancellationToken=default)
    {
        var saved=await SelectedDraft.FlushAsync(cancellationToken).ConfigureAwait(false);
        lock(_sync) { _error=null;UpdateSetSummaryLocked(saved); } Publish();return saved;
    }
    private void UpdateSetSummaryLocked(TestCaseSetSnapshot set)
    {
        var summary=new TestCaseSetSummary(set.Id,set.ProjectId,set.ProducerPluginId,set.UpdatedUtc,set.Revision,set.Name,set.Cases.Count,set.Cases.Sum(c=>c.Steps.Count));
        _sets=ModelCopy.List(_sets.Where(s=>s.Id!=set.Id).Append(summary).OrderBy(s=>s.Id,StringComparer.Ordinal));
    }
    private void SelectDraft(TestCaseSetSnapshot set)
    {
        var draft=new CaseDraftSession(_testing,set);
        draft.Changed+=_=> { bool current;lock(_sync) current=ReferenceEquals(_draft,draft);if(current) Publish(); };
        lock(_sync) { _draft=draft;UpdateSetSummaryLocked(set);_error=null; } Publish();
    }
    private async Task FlushIfSelectedAsync()
    { CaseDraftSession? draft;lock(_sync) draft=_draft;if(draft is not null) await FlushDraftAsync().ConfigureAwait(false); }
    public Task<TestCaseSetSnapshot> GenerateAsync(IReadOnlyList<string> sourceIds,CancellationToken cancellationToken=default)
        =>AdmitCaptured(()=>CaptureIds(sourceIds,64),async ids=>
        {
            await FlushIfSelectedAsync().ConfigureAwait(false);
            var sources=new List<TestSourceSnapshot>();
            foreach(var id in ids) sources.Add(await _testing.ReadSourceAsync(id,cancellationToken).ConfigureAwait(false));
            ITestCaseGenerator generator=new RuleTestCaseGenerator();
            var saved=await _testing.CreateCaseSetAsync(generator.Generate(sources),cancellationToken).ConfigureAwait(false);
            SelectDraft(saved);return ModelCopy.Set(saved);
        });
    public Task<CaseDraftView> SelectCaseSetAsync(string setId,CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        await FlushIfSelectedAsync().ConfigureAwait(false);
        var set=await _testing.ReadCaseSetAsync(setId,cancellationToken).ConfigureAwait(false);SelectDraft(set);return SelectedDraft.View;
    });
    public void CaptureSetName(string name)=>CaptureEdit(draft=>draft.CaptureName(name));
    public void CaptureCaseMetadata(string caseId,string title,string prerequisites)=>CaptureEdit(draft=>draft.CaptureCaseMetadata(caseId,title,prerequisites));
    public void CaptureStepText(string caseId,string stepId,string instruction,string expected)=>CaptureEdit(draft=>draft.CaptureStepText(caseId,stepId,instruction,expected));
    public Task<TestCaseSetSnapshot> FlushAsync(CancellationToken cancellationToken=default)=>Admit(()=>FlushDraftAsync(cancellationToken));
    public Task<TestCaseSetSnapshot> RetryDraftFlushAsync(CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        var saved=await SelectedDraft.RetryFlushAsync(cancellationToken).ConfigureAwait(false);lock(_sync) { UpdateSetSummaryLocked(saved);_error=null; } Publish();return saved;
    });
    public Task<DraftRecoveryResult> PreserveDraftAsNewAndReloadAsync(CancellationToken cancellationToken=default)=>Admit(async()=>
    {
        var result=await SelectedDraft.PreserveAsNewAndReloadAsync(cancellationToken).ConfigureAwait(false);
        var sets=await _testing.ListCaseSetsAsync(cancellationToken).ConfigureAwait(false);lock(_sync) { _sets=ModelCopy.List(sets);_error=null; } Publish();return result;
    });
    public Task<byte[]> ExportCsvAsync(CancellationToken cancellationToken=default)=>Admit(async()=>new CsvTestCaseExporter().Export(await FlushDraftAsync(cancellationToken).ConfigureAwait(false)));
}
