using Workbench.Sdk;
namespace Workbench.Testing.Domain;
public sealed record TestingControllerSnapshot(string ProjectId,IReadOnlyList<TestSourceSummary> Sources,IReadOnlyList<TestCaseSetSummary> CaseSets,IReadOnlyList<TestRunSummary> Runs,
    CaseDraftView? Draft,TestRunSnapshot? SelectedRun,string? ErrorCode,bool IsPreparing,bool IsStopped,bool OwnsRun)
{
    public bool IsAuthoringBusy { get; init; }
}
